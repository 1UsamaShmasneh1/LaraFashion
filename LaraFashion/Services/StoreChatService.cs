using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public sealed record ChatMessageMetadata(
    string? Intent = null,
    string? Size = null,
    bool AiUsed = false,
    string? AiProvider = null,
    string? AiModel = null,
    bool IsFallback = false,
    string? FallbackReason = null);

public sealed record ChatSessionListItem(
    Guid Id, DateTime StartedAtUtc, DateTime LastActivityAtUtc, string? PhoneNumber,
    int MessageCount, int AiMessageCount, int FallbackCount, string? LastIntent);

public sealed record ChatSessionPage(IReadOnlyList<ChatSessionListItem> Items, int TotalCount, int Page, int PageSize);

public sealed record BulkChatDeleteResult(int RequestedCount, int DeletedSessions);

public sealed class StoreChatService
{
    private readonly AppDbContext _db;
    private readonly AdminAuthService _adminAuthService;

    public StoreChatService(AppDbContext db, AdminAuthService adminAuthService)
    {
        _db = db;
        _adminAuthService = adminAuthService;
    }

    public async Task<Guid> AddExchangeAsync(
        Guid? sessionId,
        string userContent,
        string assistantContent,
        ChatMessageMetadata metadata,
        string? phoneNumber = null)
    {
        var now = DateTime.UtcNow;
        StoreChatSession? session = null;
        if (sessionId.HasValue)
            session = await _db.StoreChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId.Value);

        if (session is null)
        {
            session = new StoreChatSession
            {
                Id = Guid.NewGuid(),
                StartedAtUtc = now,
                LastActivityAtUtc = now
            };
            _db.StoreChatSessions.Add(session);
        }

        session.LastActivityAtUtc = now;
        if (!string.IsNullOrWhiteSpace(phoneNumber))
            session.PhoneNumber = phoneNumber;

        _db.StoreChatMessages.AddRange(
            CreateMessage(session.Id, "User", userContent, now, metadata),
            CreateMessage(session.Id, "Assistant", assistantContent, now.AddTicks(1), new()));
        await _db.SaveChangesAsync();
        return session.Id;
    }

    public async Task<ChatSessionPage> GetSessionsAsync(
        string adminToken, int page, int pageSize, bool fallbackOnly, string? search = null)
    {
        EnsureAdmin(adminToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 50);
        var query = _db.StoreChatSessions.AsNoTracking();
        if (fallbackOnly)
            query = query.Where(x => x.Messages.Any(m => m.IsFallback || m.DetectedIntent == "unknown"));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                (x.PhoneNumber != null && x.PhoneNumber.Contains(term)) ||
                x.Messages.Any(m =>
                    (m.DetectedIntent != null && m.DetectedIntent.Contains(term)) ||
                    m.Content.Contains(term)));
        }

        var total = await query.CountAsync();
        var sessions = await query
            .OrderByDescending(x => x.LastActivityAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ChatSessionListItem(
                x.Id, x.StartedAtUtc, x.LastActivityAtUtc, x.PhoneNumber,
                x.Messages.Count,
                x.Messages.Count(m => m.AiUsed),
                x.Messages.Count(m => m.IsFallback || m.DetectedIntent == "unknown"),
                x.Messages.Where(m => m.DetectedIntent != null)
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .Select(m => m.DetectedIntent)
                    .FirstOrDefault()))
            .ToListAsync();
        return new(sessions, total, page, pageSize);
    }

    public async Task<StoreChatSession?> GetChatSessionDetailsAsync(string adminToken, Guid id)
    {
        EnsureAdmin(adminToken);
        return await _db.StoreChatSessions.AsNoTracking()
            .Include(x => x.Messages.OrderBy(m => m.CreatedAtUtc))
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public Task<StoreChatSession?> GetSessionAsync(string adminToken, Guid id) =>
        GetChatSessionDetailsAsync(adminToken, id);

    public async Task DeleteSessionAsync(string adminToken, Guid id)
    {
        await DeleteChatSessionsAsync(new[] { id }, adminToken);
    }

    public async Task<BulkChatDeleteResult> DeleteChatSessionsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin(adminToken);
        ArgumentNullException.ThrowIfNull(sessionIds);

        var ids = sessionIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        const int maximumBatchSize = 500;
        if (ids.Length > maximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(sessionIds), $"A maximum of {maximumBatchSize} sessions can be deleted at once.");

        if (ids.Length == 0)
            return new(0, 0);

        // This is one atomic DELETE statement. The configured database cascade removes only
        // StoreChatMessages whose SessionId belongs to one of these sessions.
        var deletedSessions = await _db.StoreChatSessions
            .Where(session => ids.Contains(session.Id))
            .ExecuteDeleteAsync(cancellationToken);

        return new(ids.Length, deletedSessions);
    }

    private void EnsureAdmin(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_adminAuthService.IsTokenValid(token))
            throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
    }

    private static StoreChatMessage CreateMessage(
        Guid sessionId, string role, string content, DateTime createdAt, ChatMessageMetadata metadata) => new()
    {
        Id = Guid.NewGuid(), SessionId = sessionId, Role = role, Content = content, CreatedAtUtc = createdAt,
        DetectedIntent = metadata.Intent, ExtractedSize = metadata.Size, AiUsed = metadata.AiUsed,
        AiProvider = metadata.AiProvider, AiModel = metadata.AiModel, IsFallback = metadata.IsFallback,
        FallbackReason = metadata.FallbackReason
    };
}
