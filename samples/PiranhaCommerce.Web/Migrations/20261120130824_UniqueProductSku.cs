using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class UniqueProductSku : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Product_Sku",
                schema: "ecom",
                table: "Product");

            migrationBuilder.CreateIndex(
                name: "IX_Product_Sku",
                schema: "ecom",
                table: "Product",
                column: "Sku",
                unique: true,
                filter: "[Sku] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Product_Sku",
                schema: "ecom",
                table: "Product");

            migrationBuilder.CreateIndex(
                name: "IX_Product_Sku",
                schema: "ecom",
                table: "Product",
                column: "Sku");
        }
    }
}
