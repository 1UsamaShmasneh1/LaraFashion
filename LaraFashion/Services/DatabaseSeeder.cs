using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class DatabaseSeeder
{
    private readonly AppDbContext _db;
    private readonly PasswordHasherService _passwordHasher;

    public DatabaseSeeder(
        AppDbContext db,
        PasswordHasherService passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task SeedAsync()
    {
        var hasAdmin = await _db.AdminUsers.AnyAsync();

        if (hasAdmin)
            return;

        var admin = new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = _passwordHasher.HashPassword("123456"),
            CreatedAt = DateTime.UtcNow
        };

        _db.AdminUsers.Add(admin);

        await _db.SaveChangesAsync();
    }
}