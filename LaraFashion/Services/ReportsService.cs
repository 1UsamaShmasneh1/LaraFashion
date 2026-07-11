using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public enum ReportPeriod { Hourly, Daily, Weekly, Monthly, Yearly }
public sealed record SalesHistoryQuery(string Search, OrderStatus? Status, DateTime? From, DateTime? To, int Page, int PageSize);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record ReportPoint(string Label, int Orders, int Products, decimal Sales, int Visits);
public sealed record ReportSummary(int Orders, int Products, decimal Sales, int Visits);

public class ReportsService
{
    private readonly AppDbContext _db;
    private static readonly SemaphoreSlim VisitLock = new(1, 1);
    private static readonly TimeZoneInfo JerusalemTimeZone = ResolveJerusalemTimeZone();
    public ReportsService(AppDbContext db) => _db = db;

    public async Task<PagedResult<SalesHistory>> GetSalesHistoryAsync(SalesHistoryQuery request)
    {
        var query = _db.SalesHistory.AsNoTracking();
        if (request.Status is not null) query = query.Where(x => x.LastStatus == request.Status);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x => x.CustomerName.Contains(search) || x.PhoneNumber.Contains(search) || x.OrderNumber.Contains(search));
        }
        if (request.From is not null) query = query.Where(x => x.CreatedAtUtc >= LocalToUtc(request.From.Value.Date));
        if (request.To is not null) query = query.Where(x => x.CreatedAtUtc < LocalToUtc(request.To.Value.Date.AddDays(1)));
        var total = await query.CountAsync();
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 10, 100);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new(items, total, page, pageSize);
    }

    public async Task<ReportSummary> GetSummaryAsync()
    {
        var sales = _db.SalesHistory.AsNoTracking().Where(x => x.LastStatus != OrderStatus.Cancelled);
        return new(await sales.CountAsync(), await sales.SumAsync(x => (int?)x.TotalQuantity) ?? 0,
            await sales.SumAsync(x => (decimal?)x.FinalTotal) ?? 0, await _db.StoreVisits.AsNoTracking().CountAsync());
    }

    public async Task<IReadOnlyList<ReportPoint>> GetReportAsync(ReportPeriod period, bool visitsOnly = false)
    {
        var fromUtc = DateTime.UtcNow.AddYears(-5);
        var sales = visitsOnly ? new List<SalesHistory>() : await _db.SalesHistory.AsNoTracking().Where(x => x.LastStatus != OrderStatus.Cancelled && x.CreatedAtUtc >= fromUtc).ToListAsync();
        var visits = await _db.StoreVisits.AsNoTracking().Where(x => x.StartedAtUtc >= fromUtc).ToListAsync();
        var keys = sales.Select(x => PeriodKey(x.CreatedAtUtc, period)).Concat(visits.Select(x => PeriodKey(x.StartedAtUtc, period))).Distinct().OrderBy(x => x).TakeLast(36).ToList();
        return keys.Select(key => new ReportPoint(PeriodLabel(key, period), sales.Count(x => PeriodKey(x.CreatedAtUtc, period) == key),
            sales.Where(x => PeriodKey(x.CreatedAtUtc, period) == key).Sum(x => x.TotalQuantity), sales.Where(x => PeriodKey(x.CreatedAtUtc, period) == key).Sum(x => x.FinalTotal),
            visits.Count(x => PeriodKey(x.StartedAtUtc, period) == key))).ToList();
    }

    public async Task RecordVisitAsync(string visitorIdHash, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await VisitLock.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var latest = await _db.StoreVisits.OrderByDescending(x => x.LastActivityAtUtc).FirstOrDefaultAsync(x => x.VisitorIdHash == visitorIdHash, cancellationToken);
            if (latest is not null && latest.LastActivityAtUtc >= now.AddMinutes(-30)) latest.LastActivityAtUtc = now;
            else _db.StoreVisits.Add(new StoreVisit { Id = Guid.NewGuid(), VisitorIdHash = visitorIdHash, StartedAtUtc = now, LastActivityAtUtc = now });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { VisitLock.Release(); }
    }

    public static DateTime ToJerusalem(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), JerusalemTimeZone);
    private static DateTime LocalToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), JerusalemTimeZone);
    private static string PeriodKey(DateTime utc, ReportPeriod period)
    {
        var local = ToJerusalem(utc);
        if (period == ReportPeriod.Weekly) local = local.Date.AddDays(-(int)local.DayOfWeek);
        return period switch { ReportPeriod.Hourly => local.ToString("yyyy-MM-dd HH"), ReportPeriod.Daily or ReportPeriod.Weekly => local.ToString("yyyy-MM-dd"), ReportPeriod.Monthly => local.ToString("yyyy-MM"), _ => local.ToString("yyyy") };
    }
    private static string PeriodLabel(string key, ReportPeriod period) => period == ReportPeriod.Hourly ? key + ":00" : key;
    private static TimeZoneInfo ResolveJerusalemTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time"); }
    }
}
