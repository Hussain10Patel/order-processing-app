using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessingApp.Migrations;

public partial class EnforceUniqueDeliverySchedulePerOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep the latest row per OrderId before enforcing uniqueness.
        migrationBuilder.Sql(@"
DELETE FROM ""DeliverySchedules"" d
USING ""DeliverySchedules"" keep
WHERE d.""OrderId"" = keep.""OrderId""
    AND d.""Id"" < keep.""Id"";");

        migrationBuilder.DropIndex(
            name: "IX_DeliverySchedules_OrderId",
            table: "DeliverySchedules");

        migrationBuilder.CreateIndex(
            name: "IX_DeliverySchedules_OrderId",
            table: "DeliverySchedules",
            column: "OrderId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DeliverySchedules_OrderId",
            table: "DeliverySchedules");

        migrationBuilder.CreateIndex(
            name: "IX_DeliverySchedules_OrderId",
            table: "DeliverySchedules",
            column: "OrderId");
    }
}
