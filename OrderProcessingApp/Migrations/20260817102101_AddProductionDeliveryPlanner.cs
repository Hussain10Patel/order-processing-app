using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OrderProcessingApp.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionDeliveryPlanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductionDeliveryPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionDeliveryPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionDeliveryPlanEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanId = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<int>(type: "integer", nullable: true),
                    PlannedDeliveryDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionDeliveryPlanEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionDeliveryPlanEvents_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionDeliveryPlanEvents_ProductionDeliveryPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "ProductionDeliveryPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionDeliveryPlanEventLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionDeliveryPlanEventLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionDeliveryPlanEventLines_ProductionDeliveryPlanEven~",
                        column: x => x.EventId,
                        principalTable: "ProductionDeliveryPlanEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductionDeliveryPlanEventLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEventLines_EventId_ProductId",
                table: "ProductionDeliveryPlanEventLines",
                columns: new[] { "EventId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEventLines_ProductId",
                table: "ProductionDeliveryPlanEventLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEvents_OrderId",
                table: "ProductionDeliveryPlanEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEvents_PlanId_OrderId",
                table: "ProductionDeliveryPlanEvents",
                columns: new[] { "PlanId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlanEvents_PlanId_Sequence",
                table: "ProductionDeliveryPlanEvents",
                columns: new[] { "PlanId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionDeliveryPlans_Name",
                table: "ProductionDeliveryPlans",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductionDeliveryPlanEventLines");

            migrationBuilder.DropTable(
                name: "ProductionDeliveryPlanEvents");

            migrationBuilder.DropTable(
                name: "ProductionDeliveryPlans");
        }
    }
}
