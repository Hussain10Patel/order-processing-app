using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<DistributionCentre> DistributionCentres => Set<DistributionCentre>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PricePromotion> PricePromotions => Set<PricePromotion>();
    public DbSet<ProductionPlan> ProductionPlans => Set<ProductionPlan>();
    public DbSet<ProductionDecision> ProductionDecisions => Set<ProductionDecision>();
    public DbSet<ProductionDeliveryPlan> ProductionDeliveryPlans => Set<ProductionDeliveryPlan>();
    public DbSet<ProductionDeliveryPlanEvent> ProductionDeliveryPlanEvents => Set<ProductionDeliveryPlanEvent>();
    public DbSet<ProductionDeliveryPlanEventLine> ProductionDeliveryPlanEventLines => Set<ProductionDeliveryPlanEventLine>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<DeliverySchedule> DeliverySchedules => Set<DeliverySchedule>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PendingCsvImportSession> PendingCsvImportSessions => Set<PendingCsvImportSession>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL (Npgsql 7+) defaults DateTime to 'timestamp with time zone' which requires
        // Kind=Utc. Using 'timestamp without time zone' accepts any Kind so all parsed dates work.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("timestamp without time zone");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrderNumber)
                .IsUnique()
                .HasFilter("\"IsActive\" = true");
            entity.Property(x => x.OrderNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.IsAdjusted).HasDefaultValue(false);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.IsExcludedFromPlan).HasDefaultValue(false);
            entity.Property(x => x.TotalValue).HasPrecision(18, 2);
            entity.Property(x => x.TotalPallets).HasPrecision(18, 2);
            entity.HasQueryFilter(x => x.IsActive);

            entity.HasOne(x => x.DistributionCentre)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.DistributionCentreId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductCode).HasMaxLength(120);
            entity.Property(x => x.ProductName).HasMaxLength(200);
            entity.Property(x => x.Quantity).HasPrecision(18, 2);
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.Property(x => x.Pallets).HasPrecision(18, 2);
            entity.Property(x => x.IsUnmapped).HasDefaultValue(false);
            entity.Property(x => x.IsPriceMissing).HasDefaultValue(false);
            entity.Property(x => x.IsPriceMismatch).HasDefaultValue(false);
            entity.Property(x => x.IsCsvPrice).HasDefaultValue(false);
            entity.Property(x => x.MetadataJson).HasColumnType("text").HasDefaultValue("{}").IsRequired();
            entity.Ignore(x => x.Metadata);

            entity.HasOne(x => x.Order)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Product)
                .WithMany(x => x.OrderItems)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.SKUCode).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SKUCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.PalletConversionRate).HasPrecision(18, 4);
            entity.Property(x => x.IsMapped).HasDefaultValue(true);
            entity.Property(x => x.RequiresAttention).HasDefaultValue(false);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasQueryFilter(x => x.IsActive);
        });

        modelBuilder.Entity<DistributionCentre>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(120).IsRequired();
            entity.Property(x => x.RequiresAttention).HasDefaultValue(false);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.HasQueryFilter(x => x.IsActive);

            entity.HasOne(x => x.Region)
                .WithMany(x => x.DistributionCentres)
                .HasForeignKey(x => x.RegionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Region>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<PriceList>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ProductId, x.DistributionCentreId }).IsUnique();
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.HasQueryFilter(x => x.IsActive);

            entity.HasOne(x => x.Product)
                .WithMany(x => x.PriceLists)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.DistributionCentre)
                .WithMany(x => x.PriceLists)
                .HasForeignKey(x => x.DistributionCentreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PricePromotion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ProductId, x.DistributionCentreId, x.StartDate, x.EndDate })
                .IsUnique()
                .HasDatabaseName("IX_PricePromotions_ProductId_DistributionCentreId_StartDate_EndDate");
            entity.Property(x => x.PromoPrice).HasPrecision(18, 2);
            entity.Property(x => x.StartDate).IsRequired();
            entity.Property(x => x.EndDate).IsRequired();
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasQueryFilter(x => x.IsActive);

            entity.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.DistributionCentre)
                .WithMany()
                .HasForeignKey(x => x.DistributionCentreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductionPlan>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ProductId, x.Date }).IsUnique();
            entity.Property(x => x.OpeningStock).HasPrecision(18, 2);
            entity.Property(x => x.ProductionQuantity).HasPrecision(18, 2);
            entity.Property(x => x.ClosingStock).HasPrecision(18, 2);
            entity.Property(x => x.Notes).HasMaxLength(1000);

            entity.HasOne(x => x.Product)
                .WithMany(x => x.ProductionPlans)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductionDecision>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrderItemId).IsUnique();
            entity.Property(x => x.RequiredStock).HasPrecision(18, 2);
            entity.Property(x => x.CurrentStock).HasPrecision(18, 2);
            entity.Property(x => x.Difference).HasPrecision(18, 2);
            entity.Property(x => x.RequiredProductionQty).HasPrecision(18, 2);
            entity.Property(x => x.RemainingStock).HasPrecision(18, 2);
            entity.Property(x => x.Notes).HasMaxLength(1000);

            entity.HasOne(x => x.OrderItem)
                .WithMany(x => x.ProductionDecisions)
                .HasForeignKey(x => x.OrderItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductionDeliveryPlan>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });

        modelBuilder.Entity<ProductionDeliveryPlanEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PlanId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.PlanId, x.OrderId }).IsUnique();
            entity.Property(x => x.EventType).HasConversion<int>();
            entity.Property(x => x.PlannedDeliveryDate).HasColumnType("timestamp without time zone");
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();

            entity.HasOne(x => x.Plan)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Order)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Order>()
                .WithMany()
                .HasForeignKey(x => x.OwnerOrderId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });

        modelBuilder.Entity<ProductionDeliveryPlanEventLine>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EventId, x.ProductId }).IsUnique();
            entity.Property(x => x.Quantity).HasPrecision(18, 2);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();

            entity.HasOne(x => x.Event)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

            modelBuilder.Entity<Stock>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => x.ProductId).IsUnique();
                entity.Property(x => x.Quantity).HasPrecision(18, 2);
                entity.Property(x => x.LastUpdated).IsRequired();

                entity.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
            });

        modelBuilder.Entity<DeliverySchedule>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.Property(x => x.Status).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Notes).HasMaxLength(1000);

            entity.HasOne(x => x.Order)
                .WithMany(x => x.DeliverySchedules)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Entity).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Field).HasMaxLength(100).IsRequired();
            entity.Property(x => x.OldValue).HasMaxLength(500);
            entity.Property(x => x.NewValue).HasMaxLength(500);
            entity.Property(x => x.ChangedBy).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.Entity, x.EntityId });
        });

        modelBuilder.Entity<PendingCsvImportSession>(entity =>
        {
            entity.HasKey(x => x.FileId);
            entity.Property(x => x.FileId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RowsJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.MissingDistributionCentresJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.MissingProductsJson).HasColumnType("text").IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
        });
    }
}
