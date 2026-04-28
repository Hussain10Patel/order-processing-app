using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Data;

public static class SeedDataExtensions
{
    private static readonly DateTime DemoDate = DateTime.SpecifyKind(new DateTime(2026, 04, 15), DateTimeKind.Unspecified);

    public static async Task SeedCoreDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        await ApplySchemaUpdatesAsync(dbContext, cancellationToken);

        if (!await dbContext.Regions.AnyAsync(cancellationToken))
        {
            await SeedCoreReferenceDataAsync(dbContext, cancellationToken);
        }

        await SeedDemoWorkflowDataAsync(dbContext, cancellationToken);
    }

    public static Task SeedDemoWorkflowDataForDevelopmentAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        return SeedDemoWorkflowDataAsync(dbContext, cancellationToken);
    }

    private static async Task SeedCoreReferenceDataAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var northRegion = new Region { Name = "North" };
        var southRegion = new Region { Name = "South" };

        dbContext.Regions.AddRange(northRegion, southRegion);
        await dbContext.SaveChangesAsync(cancellationToken);

        var dcNorth = new DistributionCentre { Name = "DC-North-01", Code = "DC-North-01", RegionId = northRegion.Id };
        var dcSouth = new DistributionCentre { Name = "DC-South-01", Code = "DC-South-01", RegionId = southRegion.Id };

        dbContext.DistributionCentres.AddRange(dcNorth, dcSouth);

        var productA = new Product
        {
            Name = "Premium Wheat Flour 25kg",
            SKUCode = "SKU-FLOUR-25",
            PalletConversionRate = 40,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };

        var productB = new Product
        {
            Name = "Refined Sunflower Oil 5L",
            SKUCode = "SKU-OIL-5L",
            PalletConversionRate = 60,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };

        var productC = new Product
        {
            Name = "Basmati Rice 10kg",
            SKUCode = "SKU-RICE-10",
            PalletConversionRate = 50,
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        };

        dbContext.Products.AddRange(productA, productB, productC);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.PriceLists.AddRange(
            new PriceList { ProductId = productA.Id, DistributionCentreId = dcNorth.Id, Price = 18.50m },
            new PriceList { ProductId = productB.Id, DistributionCentreId = dcNorth.Id, Price = 9.25m },
            new PriceList { ProductId = productC.Id, DistributionCentreId = dcNorth.Id, Price = 14.75m },
            new PriceList { ProductId = productA.Id, DistributionCentreId = dcSouth.Id, Price = 19.00m },
            new PriceList { ProductId = productB.Id, DistributionCentreId = dcSouth.Id, Price = 9.60m },
            new PriceList { ProductId = productC.Id, DistributionCentreId = dcSouth.Id, Price = 15.10m }
        );

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
        dbContext.ProductionPlans.AddRange(
            new ProductionPlan { ProductId = productA.Id, Date = today, OpeningStock = 500, ProductionQuantity = 300, ClosingStock = 800, Notes = "Initial seed plan" },
            new ProductionPlan { ProductId = productB.Id, Date = today, OpeningStock = 600, ProductionQuantity = 200, ClosingStock = 800, Notes = "Initial seed plan" },
            new ProductionPlan { ProductId = productC.Id, Date = today, OpeningStock = 400, ProductionQuantity = 250, ClosingStock = 650, Notes = "Initial seed plan" }
        );

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedDemoWorkflowDataAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var dcNorth = await dbContext.DistributionCentres.FirstOrDefaultAsync(x => x.Name == "DC-North-01", cancellationToken);
        var dcSouth = await dbContext.DistributionCentres.FirstOrDefaultAsync(x => x.Name == "DC-South-01", cancellationToken);
        var flour = await dbContext.Products.FirstOrDefaultAsync(x => x.SKUCode == "SKU-FLOUR-25", cancellationToken);
        var oil = await dbContext.Products.FirstOrDefaultAsync(x => x.SKUCode == "SKU-OIL-5L", cancellationToken);

        if (dcNorth is null || dcSouth is null || flour is null || oil is null)
        {
            return;
        }

        await UpsertProductionPlanAsync(dbContext, flour.Id, DemoDate, 300, 220, "Demo planning stock for reporting", cancellationToken);
        await UpsertProductionPlanAsync(dbContext, oil.Id, DemoDate, 280, 180, "Demo planning stock for reporting", cancellationToken);

        await UpsertDemoOrderAsync(
            dbContext,
            orderNumber: "ORD-DEMO-APPROVED-001",
            orderDate: DemoDate.AddDays(-2),
            deliveryDate: DemoDate,
            distributionCentreId: dcNorth.Id,
            status: OrderStatus.Approved,
            notes: "Seeded demo approved order",
            items: new[]
            {
                (flour.Id, 120m, 18.50m, decimal.Round(120m / flour.PalletConversionRate, 2)),
                (oil.Id, 90m, 9.25m, decimal.Round(90m / oil.PalletConversionRate, 2))
            },
            cancellationToken);

        await UpsertDemoOrderAsync(
            dbContext,
            orderNumber: "ORD-DEMO-PROCESSED-001",
            orderDate: DemoDate.AddDays(-1),
            deliveryDate: DemoDate,
            distributionCentreId: dcSouth.Id,
            status: OrderStatus.Processed,
            notes: "Seeded demo processed order",
            items: new[]
            {
                (flour.Id, 80m, 19.00m, decimal.Round(80m / flour.PalletConversionRate, 2)),
                (oil.Id, 110m, 9.60m, decimal.Round(110m / oil.PalletConversionRate, 2))
            },
            cancellationToken);
    }

    private static async Task UpsertProductionPlanAsync(
        AppDbContext dbContext,
        int productId,
        DateTime date,
        decimal openingStock,
        decimal productionQuantity,
        string notes,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.ProductionPlans
            .FirstOrDefaultAsync(x => x.ProductId == productId && x.Date == date, cancellationToken);

        var demand = await dbContext.OrderItems
            .Where(x => x.ProductId == productId
                && x.Order != null
                && x.Order.DeliveryDate == date
                && (x.Order.Status == OrderStatus.Approved || x.Order.Status == OrderStatus.Processed))
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0m;

        var closing = openingStock + productionQuantity - demand;

        if (existing is null)
        {
            dbContext.ProductionPlans.Add(new ProductionPlan
            {
                ProductId = productId,
                Date = date,
                OpeningStock = openingStock,
                ProductionQuantity = productionQuantity,
                ClosingStock = closing,
                Notes = notes
            });
        }
        else
        {
            existing.OpeningStock = openingStock;
            existing.ProductionQuantity = productionQuantity;
            existing.ClosingStock = closing;
            existing.Notes = notes;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertDemoOrderAsync(
        AppDbContext dbContext,
        string orderNumber,
        DateTime orderDate,
        DateTime deliveryDate,
        int distributionCentreId,
        OrderStatus status,
        string notes,
        IEnumerable<(int ProductId, decimal Quantity, decimal Price, decimal Pallets)> items,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .Include(x => x.Items)
            .Include(x => x.DeliverySchedules)
            .FirstOrDefaultAsync(x => x.OrderNumber == orderNumber, cancellationToken);

        var distributionCentreExists = await dbContext.DistributionCentres
            .AsNoTracking()
            .AnyAsync(x => x.Id == distributionCentreId, cancellationToken);

        if (!distributionCentreExists)
        {
            throw new InvalidOperationException($"Distribution centre '{distributionCentreId}' not found while seeding demo orders.");
        }

        if (order is null)
        {
            order = new Order
            {
                OrderNumber = orderNumber,
                OrderDate = orderDate,
                DeliveryDate = deliveryDate,
                DistributionCentreId = distributionCentreId,
                Source = OrderSource.MANUAL,
                Status = status,
                Notes = notes,
                IsAdjusted = false
            };

            foreach (var item in items)
            {
                order.Items.Add(new OrderItem
                {
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    Price = item.Price,
                    Pallets = item.Pallets
                });
            }

            order.TotalValue = order.Items.Sum(x => x.Quantity * x.Price);
            order.TotalPallets = order.Items.Sum(x => x.Pallets);
            dbContext.Orders.Add(order);
        }
        else
        {
            order.Status = status;
            order.Notes = notes;
            order.TotalValue = order.Items.Sum(x => x.Quantity * x.Price);
            order.TotalPallets = order.Items.Sum(x => x.Pallets);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var schedule = order.DeliverySchedules.FirstOrDefault(x => x.DeliveryDate == deliveryDate);
        if (schedule is null)
        {
            dbContext.DeliverySchedules.Add(new DeliverySchedule
            {
                OrderId = order.Id,
                DeliveryDate = deliveryDate,
                Status = "Scheduled",
                Notes = "Seeded demo schedule"
            });
        }
        else
        {
            schedule.Status = "Scheduled";
            schedule.Notes = "Seeded demo schedule";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task ApplySchemaUpdatesAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""Orders"" DROP COLUMN IF EXISTS ""RegionId"";
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""IsAdjusted"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""TotalPallets"" numeric(18,2) NOT NULL DEFAULT 0;
            ALTER TABLE ""ProductionPlans"" ADD COLUMN IF NOT EXISTS ""Date"" timestamp without time zone;
            ALTER TABLE ""ProductionPlans"" ADD COLUMN IF NOT EXISTS ""ClosingStock"" numeric(18,2) NOT NULL DEFAULT 0;
            ALTER TABLE ""ProductionPlans"" ADD COLUMN IF NOT EXISTS ""Notes"" character varying(1000);
            ALTER TABLE ""DeliverySchedules"" ADD COLUMN IF NOT EXISTS ""Notes"" character varying(1000);
            ALTER TABLE ""DistributionCentres"" ADD COLUMN IF NOT EXISTS ""Code"" character varying(120) NOT NULL DEFAULT '';
            ALTER TABLE ""DistributionCentres"" ADD COLUMN IF NOT EXISTS ""RequiresAttention"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""PriceLists"" ADD COLUMN IF NOT EXISTS ""DistributionCentreId"" integer;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""ProductCode"" character varying(120);
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""ProductName"" character varying(200);
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""IsUnmapped"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""IsPriceMissing"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""IsPriceMismatch"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""IsCsvPrice"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""OrderItems"" ADD COLUMN IF NOT EXISTS ""MetadataJson"" text NOT NULL DEFAULT '{{}}';
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""IsMapped"" boolean NOT NULL DEFAULT TRUE;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""RequiresAttention"" boolean NOT NULL DEFAULT FALSE;
            ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""CreatedAt"" timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP;
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""OrderItems"" AS oi
            SET
                ""ProductCode"" = COALESCE(NULLIF(BTRIM(oi.""ProductCode""), ''), p.""SKUCode""),
                ""ProductName"" = COALESCE(NULLIF(BTRIM(oi.""ProductName""), ''), p.""Name""),
                ""IsUnmapped"" = COALESCE(oi.""IsUnmapped"", FALSE) OR COALESCE(NOT p.""IsMapped"", FALSE)
            FROM ""Products"" AS p
            WHERE p.""Id"" = oi.""ProductId"";
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""Products""
            SET ""CreatedAt"" = CURRENT_TIMESTAMP
            WHERE ""CreatedAt"" IS NULL;
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = 'public'
                      AND indexname = 'IX_Products_SKUCode_Normalized')
                THEN
                    IF NOT EXISTS (
                        SELECT LOWER(BTRIM(""SKUCode""))
                        FROM ""Products""
                        GROUP BY LOWER(BTRIM(""SKUCode""))
                        HAVING COUNT(*) > 1)
                    THEN
                        EXECUTE 'CREATE UNIQUE INDEX ""IX_Products_SKUCode_Normalized"" ON ""Products"" (LOWER(BTRIM(""SKUCode"")))';
                    END IF;
                END IF;
            END $$;
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""DistributionCentres""
            SET ""Code"" = COALESCE(NULLIF(BTRIM(""Code""), ''), BTRIM(""Name""), 'UNMAPPED-DC')
            WHERE COALESCE(NULLIF(BTRIM(""Code""), ''), '') = '';

            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DistributionCentres_Code"" ON ""DistributionCentres"" (""Code"");
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                        AND table_name = 'PriceLists'
                        AND column_name = 'RegionId')
                THEN
                    EXECUTE '
                        UPDATE ""PriceLists"" AS pl
                        SET ""DistributionCentreId"" = dc_map.""Id""
                        FROM (
                            SELECT DISTINCT ON (""RegionId"")
                                ""RegionId"",
                                ""Id""
                            FROM ""DistributionCentres""
                            ORDER BY ""RegionId"", ""Id""
                        ) AS dc_map
                        WHERE pl.""DistributionCentreId"" IS NULL
                            AND pl.""RegionId"" = dc_map.""RegionId"";
                    ';
                END IF;
            END $$;

            UPDATE ""PriceLists""
            SET ""DistributionCentreId"" = (
                SELECT ""Id""
                FROM ""DistributionCentres""
                ORDER BY ""Id""
                LIMIT 1
            )
            WHERE ""DistributionCentreId"" IS NULL;

            ALTER TABLE ""PriceLists"" ALTER COLUMN ""DistributionCentreId"" SET NOT NULL;

            ALTER TABLE ""PriceLists"" DROP CONSTRAINT IF EXISTS ""FK_PriceLists_DistributionCentres_DistributionCentreId"";
            ALTER TABLE ""PriceLists""
                ADD CONSTRAINT ""FK_PriceLists_DistributionCentres_DistributionCentreId""
                FOREIGN KEY (""DistributionCentreId"")
                REFERENCES ""DistributionCentres"" (""Id"") ON DELETE CASCADE;

            DROP INDEX IF EXISTS ""IX_PriceLists_ProductId_RegionId"";
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PriceLists_ProductId_DistributionCentreId""
                ON ""PriceLists"" (""ProductId"", ""DistributionCentreId"");

            ALTER TABLE ""PriceLists"" DROP COLUMN IF EXISTS ""RegionId"";
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            UPDATE ""Orders""
            SET ""TotalPallets"" = COALESCE(order_totals.""TotalPallets"", 0)
            FROM (
                SELECT ""OrderId"", SUM(""Pallets"") AS ""TotalPallets""
                FROM ""OrderItems""
                GROUP BY ""OrderId""
            ) AS order_totals
            WHERE order_totals.""OrderId"" = ""Orders"".""Id"";

            UPDATE ""Orders""
            SET ""TotalPallets"" = 0
            WHERE ""Id"" NOT IN (SELECT DISTINCT ""OrderId"" FROM ""OrderItems"");
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
                ""Id"" serial NOT NULL,
                ""Entity"" character varying(100) NOT NULL,
                ""EntityId"" integer NOT NULL,
                ""Field"" character varying(100) NOT NULL,
                ""OldValue"" character varying(500),
                ""NewValue"" character varying(500),
                ""ChangedBy"" character varying(200) NOT NULL DEFAULT 'System',
                ""CreatedAt"" timestamp without time zone NOT NULL,
                CONSTRAINT ""PK_AuditLogs"" PRIMARY KEY (""Id"")
            );
            CREATE INDEX IF NOT EXISTS ""IX_AuditLogs_Entity_EntityId"" ON ""AuditLogs"" (""Entity"", ""EntityId"");
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""PendingCsvImportSessions"" (
                ""FileId"" character varying(64) NOT NULL,
                ""RowsJson"" text NOT NULL,
                ""AllowDuplicates"" boolean NOT NULL,
                ""MissingDistributionCentresJson"" text NOT NULL,
                ""MissingProductsJson"" text NOT NULL DEFAULT '[]',
                ""CreatedAtUtc"" timestamp without time zone NOT NULL,
                CONSTRAINT ""PK_PendingCsvImportSessions"" PRIMARY KEY (""FileId"")
            );
            ALTER TABLE ""PendingCsvImportSessions"" ADD COLUMN IF NOT EXISTS ""MissingProductsJson"" text NOT NULL DEFAULT '[]';
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                        AND table_name = 'ProductionPlans'
                        AND column_name = 'ProductionDate')
                THEN
                    EXECUTE 'UPDATE ""ProductionPlans"" SET ""Date"" = COALESCE(""Date"", ""ProductionDate"", CURRENT_DATE) WHERE ""Date"" IS NULL';
                ELSE
                    EXECUTE 'UPDATE ""ProductionPlans"" SET ""Date"" = COALESCE(""Date"", CURRENT_DATE) WHERE ""Date"" IS NULL';
                END IF;
            END $$;

            ALTER TABLE ""ProductionPlans"" ALTER COLUMN ""Date"" SET NOT NULL;
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                        AND table_name = 'ProductionPlans'
                        AND column_name = 'ProductionDate')
                THEN
                    EXECUTE 'UPDATE ""ProductionPlans"" SET ""ProductionDate"" = COALESCE(""ProductionDate"", ""Date"", CURRENT_DATE) WHERE ""ProductionDate"" IS NULL';
                    EXECUTE 'ALTER TABLE ""ProductionPlans"" ALTER COLUMN ""ProductionDate"" SET DEFAULT CURRENT_DATE';
                END IF;
            END $$;
        ", cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ProductionPlans_ProductId_Date""
            ON ""ProductionPlans"" (""ProductId"", ""Date"");
        ", cancellationToken);
    }
}
