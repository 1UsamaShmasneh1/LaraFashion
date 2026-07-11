using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public enum ReportPeriod { Hourly, Daily, Weekly, Monthly, Yearly }
public sealed record SalesHistoryQuery(string Search, OrderStatus? Status, DateTime? From, DateTime? To, int Page, int PageSize);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record ReportPoint(string Key, string Label, string FullLabel, int Orders, int Products, decimal Sales, int Visits);
public sealed record ReportResult(IReadOnlyList<ReportPoint> Points, int Orders, int Products, decimal Sales, int Visits);
public sealed record ReportSummary(int Orders, int Products, decimal Sales, int Visits);
public sealed record LegacyOrdersImportPreview(int Eligible, int Sandbox, int AlreadyImported);
public sealed record LegacyOrdersImportResult(int Imported, int Sandbox, int AlreadyImported, int Failed);

public class ReportsService
{
    private readonly AppDbContext _db;
    private readonly AdminAuthService _adminAuthService;
    private static readonly SemaphoreSlim VisitLock = new(1, 1);
    private static readonly SemaphoreSlim LegacyImportLock = new(1, 1);
    private static readonly TimeZoneInfo JerusalemTimeZone = ResolveJerusalemTimeZone();
    public ReportsService(AppDbContext db, AdminAuthService adminAuthService)
    {
        _db = db;
        _adminAuthService = adminAuthService;
    }

    public async Task<LegacyOrdersImportPreview> GetLegacyOrdersImportPreviewAsync(string adminToken)
    {
        EnsureAdmin(adminToken);
        var sandbox = await _db.Orders.AsNoTracking().CountAsync(x => x.IsSandbox);
        var alreadyImported = await _db.Orders.AsNoTracking()
            .CountAsync(x => !x.IsSandbox && _db.SalesHistory.Any(h => h.OriginalOrderId == x.Id));
        var eligible = await _db.Orders.AsNoTracking()
            .CountAsync(x => !x.IsSandbox && !_db.SalesHistory.Any(h => h.OriginalOrderId == x.Id));
        return new(eligible, sandbox, alreadyImported);
    }

    public async Task<LegacyOrdersImportResult> ImportLegacyOrdersAsync(string adminToken)
    {
        EnsureAdmin(adminToken);
        await LegacyImportLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            var sandbox = await _db.Orders.AsNoTracking().CountAsync(x => x.IsSandbox);
            var alreadyImported = await _db.Orders.AsNoTracking()
                .CountAsync(x => !x.IsSandbox && _db.SalesHistory.Any(h => h.OriginalOrderId == x.Id));

            var candidates = await _db.Orders.AsNoTracking()
                .Where(x => !x.IsSandbox && !_db.SalesHistory.Any(h => h.OriginalOrderId == x.Id))
                .Select(x => new
                {
                    x.Id,
                    x.OrderNumber,
                    x.CreatedAt,
                    x.UpdatedAt,
                    x.Status,
                    x.FinalTotal,
                    x.OriginalTotal,
                    x.DiscountAmount,
                    CustomerName = x.Customer.FullName,
                    PhoneNumber = x.Customer.PhoneNumber,
                    Items = x.Items.Select(i => new { i.Quantity, i.UnitPrice }).ToList()
                })
                .ToListAsync();

            var rows = candidates.Select(x => new SalesHistory
            {
                Id = Guid.NewGuid(),
                OriginalOrderId = x.Id,
                OrderNumber = x.OrderNumber,
                CreatedAtUtc = x.CreatedAt.ToUniversalTime(),
                CustomerName = x.CustomerName,
                PhoneNumber = x.PhoneNumber,
                TotalQuantity = x.Items.Sum(i => i.Quantity),
                FinalTotal = HasStoredFinalTotal(x.FinalTotal, x.OriginalTotal, x.DiscountAmount)
                    ? x.FinalTotal
                    : x.Items.Sum(i => i.UnitPrice * i.Quantity),
                LastStatus = x.Status,
                StatusUpdatedAtUtc = x.UpdatedAt.ToUniversalTime()
            }).ToList();

            _db.SalesHistory.AddRange(rows);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(rows.Count, sandbox, alreadyImported, 0);
        }
        finally
        {
            LegacyImportLock.Release();
        }
    }

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

    public async Task<ReportResult> GetReportAsync(ReportPeriod period, DateTime fromLocal, DateTime toLocal, bool visitsOnly = false, CancellationToken cancellationToken = default)
    {
        var fromUtc = LocalToUtc(fromLocal);
        var toUtc = LocalToUtc(toLocal);
        var sales = visitsOnly
            ? []
            : await _db.SalesHistory.AsNoTracking()
                .Where(x => x.LastStatus != OrderStatus.Cancelled && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc)
                .Select(x => new ReportSaleRow(x.CreatedAtUtc, x.TotalQuantity, x.FinalTotal))
                .ToListAsync(cancellationToken);
        var visits = await _db.StoreVisits.AsNoTracking()
            .Where(x => x.StartedAtUtc >= fromUtc && x.StartedAtUtc < toUtc)
            .Select(x => x.StartedAtUtc)
            .ToListAsync(cancellationToken);

        var salesGroups = sales.GroupBy(x => PeriodKey(x.CreatedAtUtc, period)).ToDictionary(x => x.Key, x => x.ToList());
        var visitGroups = visits.GroupBy(x => PeriodKey(x, period)).ToDictionary(x => x.Key, x => x.Count());
        var keys = salesGroups.Keys.Concat(visitGroups.Keys).Distinct().OrderBy(x => x).ToList();
        var points = keys.Select(key =>
        {
            salesGroups.TryGetValue(key, out var periodSales);
            periodSales ??= [];
            return new ReportPoint(key, PeriodLabel(key, period), PeriodFullLabel(key, period), periodSales.Count,
                periodSales.Sum(x => x.TotalQuantity), periodSales.Sum(x => x.FinalTotal), visitGroups.GetValueOrDefault(key));
        }).ToList();
        return new(points, sales.Count, sales.Sum(x => x.TotalQuantity), sales.Sum(x => x.FinalTotal), visits.Count);
    }

    public async Task<IReadOnlyList<ReportPoint>> GetReportAsync(ReportPeriod period, bool visitsOnly = false) =>
        (await GetReportAsync(period, DateTime.Today.AddYears(-5), DateTime.Today.AddDays(1), visitsOnly)).Points;

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
    private static string PeriodLabel(string key, ReportPeriod period)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ar");
        return period switch
        {
            ReportPeriod.Hourly => DateTime.ParseExact(key, "yyyy-MM-dd HH", null).ToString("HH:mm"),
            ReportPeriod.Daily => DateTime.ParseExact(key, "yyyy-MM-dd", null).ToString("dd/MM"),
            ReportPeriod.Weekly => $"الأسبوع {System.Globalization.ISOWeek.GetWeekOfYear(DateTime.ParseExact(key, "yyyy-MM-dd", null))}",
            ReportPeriod.Monthly => DateTime.ParseExact(key, "yyyy-MM", null).ToString("MMMM yyyy", culture),
            _ => key
        };
    }
    private static string PeriodFullLabel(string key, ReportPeriod period)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ar");
        var date = DateTime.ParseExact(key, period switch { ReportPeriod.Hourly => "yyyy-MM-dd HH", ReportPeriod.Daily or ReportPeriod.Weekly => "yyyy-MM-dd", ReportPeriod.Monthly => "yyyy-MM", _ => "yyyy" }, null);
        return period switch
        {
            ReportPeriod.Hourly => date.ToString("dddd، dd MMMM yyyy - HH:00", culture),
            ReportPeriod.Daily => date.ToString("dddd، dd MMMM yyyy", culture),
            ReportPeriod.Weekly => $"الأسبوع {System.Globalization.ISOWeek.GetWeekOfYear(date)} - {date:yyyy}",
            ReportPeriod.Monthly => date.ToString("MMMM yyyy", culture),
            _ => date.ToString("yyyy")
        };
    }
    private sealed record ReportSaleRow(DateTime CreatedAtUtc, int TotalQuantity, decimal FinalTotal);
    private void EnsureAdmin(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_adminAuthService.IsTokenValid(token))
            throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
    }
    private static bool HasStoredFinalTotal(decimal finalTotal, decimal originalTotal, decimal discountAmount) =>
        finalTotal != 0 || originalTotal != 0 || discountAmount != 0;
    private static TimeZoneInfo ResolveJerusalemTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time"); }
    }
}
