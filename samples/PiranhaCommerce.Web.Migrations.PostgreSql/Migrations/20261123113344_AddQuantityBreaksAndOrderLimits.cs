using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddQuantityBreaksAndOrderLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxOrderQuantity",
                schema: "ecom",
                table: "ProductListing",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinOrderQuantity",
                schema: "ecom",
                table: "ProductListing",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductPriceBreak",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinQuantity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPriceBreak", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPriceBreak_CustomerGroup_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "customer",
                        principalTable: "CustomerGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductPriceBreak_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceBreak_GroupId",
                schema: "ecom",
                table: "ProductPriceBreak",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceBreak_ProductId_GroupId_MinQuantity",
                schema: "ecom",
                table: "ProductPriceBreak",
                columns: new[] { "ProductId", "GroupId", "MinQuantity" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductPriceBreak",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "MaxOrderQuantity",
                schema: "ecom",
                table: "ProductListing");

            migrationBuilder.DropColumn(
                name: "MinOrderQuantity",
                schema: "ecom",
                table: "ProductListing");
        }
    }
}
