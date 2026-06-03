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

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<CustomerInfo> Customers => Set<CustomerInfo>();

    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    public DbSet<Discount> Discounts => Set<Discount>();

    public DbSet<ProductDiscount> ProductDiscounts => Set<ProductDiscount>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

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
    }
}
