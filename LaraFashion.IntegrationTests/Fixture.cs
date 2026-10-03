using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

sealed class Fixture : IDbContextFactory<AppDbContext>
{
    public static string TestRoot { get; } = Path.Combine(Path.GetTempPath(), "LaraFashionTests", Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<AppDbContext> options;
    private readonly IConfiguration config;
    public AppStoragePaths Paths { get; }
    public string Token => new JwtService(config).GenerateAdminToken("test-admin");
    public IConfiguration Configuration => config;
    private Fixture()
    {
        var root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Paths = new(Path.Combine(root, "test.db"), Path.Combine(root, "uploads")); Directory.CreateDirectory(Paths.UploadsPath);
        options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Paths.DatabasePath};Pooling=False;Default Timeout=10").Options;
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = "integration-test-only-key-012345678901234567890", ["JwtSettings:Issuer"] = "tests",
            ["JwtSettings:Audience"] = "tests", ["JwtSettings:ExpiryMinutes"] = "60"
        }).Build();
    }
    public static async Task<Fixture> Create(bool previous = false)
    {
        var f = new Fixture(); await using var db = f.Db();
        if (previous) await db.GetService<IMigrator>().MigrateAsync("20260825203549_AddStoreChatSettings");
        else await db.Database.MigrateAsync();
        await new DatabaseSeeder(db, new PasswordHasherService()).SeedAsync();
        return f;
    }
    public LaraFashion.Data.AppDbContext Db() => new(options);
    public AppDbContext CreateDbContext() => Db();
    public AdminAuthService Auth(AppDbContext db) => new(db, new JwtService(config), new PasswordHasherService());
    public OrderService Orders(AppDbContext db) => new(Auth(db), Paths, this);
    public ProductService Products(AppDbContext db) => new(db, Paths, this);
    public CustomerInfo Customer() => new() { Id = Guid.NewGuid(), FullName = "Test", PhoneNumber = "0501234567", City = "Test" };
    public CartItem Item(Product p, string size = "S", int q = 1) => new() { ProductId = p.Id, ProductName = "Test", Size = size, Quantity = q, UnitPrice = 10 };
    public CartDiscountResult Total(int q = 1) => new() { OriginalTotal = 10 * q };
    public async Task<Product> Product(params int[] quantities)
    {
        await using var db = Db();
        var p = new Product { Id = Guid.NewGuid(), Name = "Test", OriginalPrice = 10,
            Sizes = quantities.Select((q, i) => new ProductSize { Id = Guid.NewGuid(), SizeName = new[] { "S", "M", "L" }[i], Quantity = q }).ToList(),
            Images = [new() { Id = Guid.NewGuid(), ImageUrl = "/test-one.webp", IsPrimary = true }, new() { Id = Guid.NewGuid(), ImageUrl = "/test-two.webp", SortOrder = 1 }] };
        db.Products.Add(p); await db.SaveChangesAsync(); return p;
    }
    public async Task<Order> Buy(Product p, string size = "S", int q = 1, Guid? attempt = null, bool sandbox = false)
    { await using var db = Db(); return await Orders(db).CreateOrderAsync(Customer(), [Item(p, size, q)], Total(q), sandbox, attempt); }
    public async Task<int> Quantity(Product p, string size = "S")
    { await using var db = Db(); return await db.ProductSizes.Where(x => x.ProductId == p.Id && x.SizeName == size).Select(x => x.Quantity).SingleAsync(); }
    public async Task<int[]> Quantities(Product p)
    { await using var db = Db(); return await db.ProductSizes.Where(x => x.ProductId == p.Id).OrderBy(x => x.SizeName == "S" ? 0 : x.SizeName == "M" ? 1 : 2).Select(x => x.Quantity).ToArrayAsync(); }
    public async Task SeedLegacy(string number, bool sandbox)
    {
        await using var db = Db(); var orderId = Guid.NewGuid(); var customer = Customer();
        db.Customers.Add(customer); await db.SaveChangesAsync(); var now = DateTime.Now;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Orders (Id, OrderNumber, CustomerId, Status, CreatedAt, UpdatedAt, OriginalTotal, DiscountAmount, DiscountName, FinalTotal, IsSandbox) VALUES ({orderId}, {number}, {customer.Id}, {0}, {now}, {now}, {20m}, {5m}, {"Test"}, {15m}, {sandbox})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id, OrderId, ProductId, ProductName, ProductSerialNumber, ProductImageUrl, Size, Quantity, UnitPrice) VALUES ({Guid.NewGuid()}, {orderId}, {Guid.NewGuid()}, {"Test"}, {string.Empty}, {string.Empty}, {"S"}, {2}, {10m})");
        if (!sandbox) db.SalesHistory.Add(new() { Id = Guid.NewGuid(), OriginalOrderId = orderId, OrderNumber = number, CreatedAtUtc = now.ToUniversalTime(), FinalTotal = 15, TotalQuantity = 2 });
        await db.SaveChangesAsync();
    }
}
