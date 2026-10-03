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

    public DbSet<OrderNumberReservation> OrderNumberReservations => Set<OrderNumberReservation>();
    public DbSet<CheckoutAttempt> CheckoutAttempts => Set<CheckoutAttempt>();
    public DbSet<OrderNumberAudit> OrderNumberAudits => Set<OrderNumberAudit>();

    public DbSet<StoreVisit> StoreVisits => Set<StoreVisit>();

    public DbSet<StoreChatSession> StoreChatSessions => Set<StoreChatSession>();

    public DbSet<StoreChatMessage> StoreChatMessages => Set<StoreChatMessage>();

    public DbSet<StoreChatSettings> StoreChatSettings => Set<StoreChatSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderNumberReservation>().HasKey(x => x.Number);
        modelBuilder.Entity<OrderNumberReservation>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<CheckoutAttempt>().HasIndex(x => x.OrderId).IsUnique();
        modelBuilder.Entity<OrderNumberAudit>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<Order>().Property(x => x.NumberRevision).IsConcurrencyToken();
        modelBuilder.Entity<ProductSize>().Property(x => x.Quantity).IsConcurrencyToken();

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

        modelBuilder.Entity<StoreChatSession>(entity =>
        {
            entity.HasIndex(x => x.LastActivityAtUtc);
            entity.HasMany(x => x.Messages)
                .WithOne(x => x.Session)
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoreChatMessage>(entity =>
        {
            entity.HasIndex(x => new { x.SessionId, x.CreatedAtUtc });
            entity.HasIndex(x => x.DetectedIntent);
            entity.HasIndex(x => x.IsFallback);
        });

        modelBuilder.Entity<StoreChatSettings>(entity =>
        {
            entity.ToTable("StoreChatSettings");
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.IsCustomerChatEnabled).HasDefaultValue(false);
        });
    }
}
