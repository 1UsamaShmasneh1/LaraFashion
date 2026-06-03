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
        await EnsureCategorySchemaAsync();

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

    private async Task EnsureCategorySchemaAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""Categories"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Categories"" PRIMARY KEY,
    ""Name"" TEXT NOT NULL,
    ""IsActive"" INTEGER NOT NULL,
    ""CreatedAt"" TEXT NOT NULL,
    ""UpdatedAt"" TEXT NOT NULL
);");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS ""ProductCategories"" (
    ""ProductId"" TEXT NOT NULL,
    ""CategoryId"" TEXT NOT NULL,
    CONSTRAINT ""PK_ProductCategories"" PRIMARY KEY (""ProductId"", ""CategoryId""),
    CONSTRAINT ""FK_ProductCategories_Products_ProductId"" FOREIGN KEY (""ProductId"") REFERENCES ""Products"" (""Id"") ON DELETE CASCADE,
    CONSTRAINT ""FK_ProductCategories_Categories_CategoryId"" FOREIGN KEY (""CategoryId"") REFERENCES ""Categories"" (""Id"") ON DELETE CASCADE
);");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE INDEX IF NOT EXISTS ""IX_ProductCategories_CategoryId"" ON ""ProductCategories"" (""CategoryId"");");
    }
}
