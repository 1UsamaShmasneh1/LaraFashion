using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class AdminAuthService
{
    private readonly AppDbContext _db;
    private readonly JwtService _jwtService;
    private readonly PasswordHasherService _passwordHasher;

    public AdminAuthService(
        AppDbContext db,
        JwtService jwtService,
        PasswordHasherService passwordHasher)
    {
        _db = db;
        _jwtService = jwtService;
        _passwordHasher = passwordHasher;
    }

    public async Task<string?> LoginAsync(string username, string password)
    {
        var admin = await _db.AdminUsers
            .FirstOrDefaultAsync(x => x.Username == username);

        if (admin is null)
            return null;

        var isValid = _passwordHasher.VerifyPassword(
            password,
            admin.PasswordHash);

        if (!isValid)
            return null;

        return _jwtService.GenerateAdminToken(admin.Username);
    }

    public bool IsTokenValid(string token)
    {
        return _jwtService.ValidateAdminToken(token);
    }
}