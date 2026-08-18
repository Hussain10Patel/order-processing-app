using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessingApp.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerOrderIdAndPlanExclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerOrderId",
                table: "ProductionDeliveryPlanEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExcludedFromPlan",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEvents_OwnerOrderId",
                table: "ProductionDeliveryPlanEvents",
                column: "OwnerOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductionDeliveryPlanEvents_Orders_OwnerOrderId",
                table: "ProductionDeliveryPlanEvents",
                column: "OwnerOrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductionDeliveryPlanEvents_Orders_OwnerOrderId",
                table: "ProductionDeliveryPlanEvents");

            migrationBuilder.DropIndex(
                name: "IX_ProductionDeliveryPlanEvents_OwnerOrderId",
                table: "ProductionDeliveryPlanEvents");

            migrationBuilder.DropColumn(
                name: "OwnerOrderId",
                table: "ProductionDeliveryPlanEvents");

            migrationBuilder.DropColumn(
                name: "IsExcludedFromPlan",
                table: "Orders");
        }
    }
}
