using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductSize> ProductSizes => Set<ProductSize>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<CustomerInfo> Customers => Set<CustomerInfo>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<Discount> Discounts => Set<Discount>();

    public DbSet<ProductDiscount> ProductDiscounts => Set<ProductDiscount>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

    public DbSet<SalesHistory> SalesHistory => Set<SalesHistory>();

    public DbSet<StoreVisit> StoreVisits => Set<StoreVisit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProductDiscount>()
            .HasKey(x => new { x.ProductId, x.DiscountId });

        modelBuilder.Entity<ProductDiscount>()
            .HasOne(x => x.Product)
            .WithMany(x => x.ProductDiscounts)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProductImage>(entity =>
        {
            entity.HasOne(x => x.Product)
                .WithMany(x => x.Images)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.ProductId, x.SortOrder });
        });

        modelBuilder.Entity<ProductDiscount>()
            .HasOne(x => x.Discount)
            .WithMany(x => x.ProductDiscounts)
            .HasForeignKey(x => x.DiscountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProductCategory>()
            .HasKey(x => new { x.ProductId, x.CategoryId });

        modelBuilder.Entity<ProductCategory>()
            .HasOne(x => x.Product)
            .WithMany(x => x.ProductCategories)
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProductCategory>()
            .HasOne(x => x.Category)
            .WithMany(x => x.ProductCategories)
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Category>()
            .ToTable("Categories", x => x.ExcludeFromMigrations());

        modelBuilder.Entity<ProductCategory>()
            .ToTable("ProductCategories", x => x.ExcludeFromMigrations());

        modelBuilder.Entity<SalesHistory>(entity =>
        {
            entity.HasIndex(x => x.OriginalOrderId).IsUnique();
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.LastStatus);
            entity.HasIndex(x => x.PhoneNumber);
        });

        modelBuilder.Entity<StoreVisit>(entity =>
        {
            entity.HasIndex(x => x.StartedAtUtc);
            entity.HasIndex(x => new { x.VisitorIdHash, x.LastActivityAtUtc });
        });
    }
}
