using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessingApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveredAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnRouteAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpectedDeliveryAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedDeliveryDurationHours",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeliveryEstimated",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginalCsvDeliveryDate",
                table: "Orders",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_IsAssignedToProduction",
                table: "Orders",
                columns: new[] { "Status", "IsAssignedToProduction" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status_IsAssignedToProduction",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "EnRouteAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExpectedDeliveryAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExpectedDeliveryDurationHours",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsDeliveryEstimated",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OriginalCsvDeliveryDate",
                table: "Orders");
        }
    }
}
