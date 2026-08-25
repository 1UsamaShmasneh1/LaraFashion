using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public sealed class StoreChatSettingsService
{
    private readonly AppDbContext _db;
    private readonly AdminAuthService _adminAuthService;

    public StoreChatSettingsService(AppDbContext db, AdminAuthService adminAuthService)
    {
        _db = db;
        _adminAuthService = adminAuthService;
    }

    public async Task<bool> GetCustomerChatEnabledAsync(CancellationToken cancellationToken = default)
    {
        var value = await _db.StoreChatSettings
            .AsNoTracking()
            .Where(x => x.Id == StoreChatSettings.SingletonId)
            .Select(x => (bool?)x.IsCustomerChatEnabled)
            .SingleOrDefaultAsync(cancellationToken);

        return value ?? false;
    }

    public async Task SetCustomerChatEnabledAsync(
        bool enabled,
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin(adminToken);

        var settings = await _db.StoreChatSettings
            .SingleOrDefaultAsync(x => x.Id == StoreChatSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            settings = new StoreChatSettings { Id = StoreChatSettings.SingletonId };
            _db.StoreChatSettings.Add(settings);
        }

        settings.IsCustomerChatEnabled = enabled;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void EnsureAdmin(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_adminAuthService.IsTokenValid(token))
            throw new UnauthorizedAccessException("انتهت صلاحية جلسة الإدارة.");
    }
}
