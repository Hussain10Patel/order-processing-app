using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessingApp.Migrations;

public partial class AddProductionDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductionDecisions",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrderItemId = table.Column<int>(type: "integer", nullable: false),
                IsSufficient = table.Column<bool>(type: "boolean", nullable: false),
                RequiredProductionQty = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductionDecisions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProductionDecisions_OrderItems_OrderItemId",
                    column: x => x.OrderItemId,
                    principalTable: "OrderItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProductionDecisions_OrderItemId",
            table: "ProductionDecisions",
            column: "OrderItemId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProductionDecisions");
    }
}
