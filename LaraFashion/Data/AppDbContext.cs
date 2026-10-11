using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;
using LaraFashion.Services;

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
    public DbSet<HistoricalOrderItemEvidence> HistoricalOrderItemEvidence => Set<HistoricalOrderItemEvidence>();
    public DbSet<OrderInventoryReview> OrderInventoryReviews => Set<OrderInventoryReview>();
    public DbSet<OrderInventoryReviewLine> OrderInventoryReviewLines => Set<OrderInventoryReviewLine>();
    public DbSet<OrderInventoryReviewMovement> OrderInventoryReviewMovements => Set<OrderInventoryReviewMovement>();

    public DbSet<StoreVisit> StoreVisits => Set<StoreVisit>();

    public DbSet<StoreChatSession> StoreChatSessions => Set<StoreChatSession>();

    public DbSet<StoreChatMessage> StoreChatMessages => Set<StoreChatMessage>();

    public DbSet<StoreChatSettings> StoreChatSettings => Set<StoreChatSettings>();

    private void PrepareSizeKeys()
    {
        foreach (var entry in ChangeTracker.Entries<ProductSize>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            entry.Entity.NormalizedName = SizeNamePolicy.Key(entry.Entity.SizeName);
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { PrepareSizeKeys(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { PrepareSizeKeys(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderNumberReservation>().HasKey(x => x.Number);
        modelBuilder.Entity<OrderNumberReservation>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<CheckoutAttempt>().HasIndex(x => x.OrderId).IsUnique();
        modelBuilder.Entity<OrderNumberAudit>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<Order>().Property(x => x.NumberRevision).IsConcurrencyToken();
        modelBuilder.Entity<Order>().Property(x => x.InventoryRevision).IsConcurrencyToken();
        modelBuilder.Entity<HistoricalOrderItemEvidence>().HasKey(x => x.OrderItemId);
        modelBuilder.Entity<HistoricalOrderItemEvidence>().HasIndex(x => x.OrderId);
        modelBuilder.Entity<OrderInventoryReview>().HasKey(x => x.OperationId);
        modelBuilder.Entity<OrderInventoryReview>().HasIndex(x => new { x.OrderId, x.NewRevision }).IsUnique();
        modelBuilder.Entity<OrderInventoryReviewLine>().HasKey(x => new { x.OperationId, x.OrderItemId });
        modelBuilder.Entity<OrderInventoryReviewLine>().HasOne<OrderInventoryReview>().WithMany(x => x.Lines)
            .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<OrderInventoryReviewMovement>().HasKey(x => new { x.OperationId, x.TargetSizeId });
        modelBuilder.Entity<OrderInventoryReviewMovement>().HasOne<OrderInventoryReview>().WithMany(x => x.Movements)
            .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProductSize>().Property(x => x.Quantity).IsConcurrencyToken();
        modelBuilder.Entity<ProductSize>().Property(x => x.ReviewRevision).IsConcurrencyToken();
        modelBuilder.Entity<ProductSize>().HasIndex(x => new { x.ProductId, x.NormalizedName }).IsUnique();
        modelBuilder.Entity<ProductSize>().Property(x => x.NormalizedName).UseCollation("BINARY");

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
