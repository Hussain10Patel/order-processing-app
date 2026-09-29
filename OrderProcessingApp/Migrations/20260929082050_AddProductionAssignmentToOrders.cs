using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessingApp.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionAssignmentToOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAssignedToProduction",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAssignedToProduction",
                table: "Orders");
        }
    }
}
