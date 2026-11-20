using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class MoveProductToCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Product moves from the ecommerce schema to the shared catalogue schema (table is moved, not recreated).
            migrationBuilder.EnsureSchema(name: "catalog");
            migrationBuilder.RenameTable(name: "Product", schema: "ecom", newName: "Product", newSchema: "catalog");

            migrationBuilder.CreateTable(
                name: "ProductListing",
                schema: "ecom",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Published = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductListing", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_ProductListing_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Carry the existing online flag over before the column is dropped.
            migrationBuilder.Sql("INSERT INTO [ecom].[ProductListing] ([ProductId], [Published]) SELECT [Id], [Published] FROM [catalog].[Product]");

            migrationBuilder.DropColumn(name: "Published", schema: "catalog", table: "Product");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Published", schema: "catalog", table: "Product", type: "bit", nullable: false, defaultValue: false);

            migrationBuilder.Sql("UPDATE p SET p.[Published] = l.[Published] FROM [catalog].[Product] p INNER JOIN [ecom].[ProductListing] l ON l.[ProductId] = p.[Id]");

            migrationBuilder.DropTable(name: "ProductListing", schema: "ecom");
            migrationBuilder.RenameTable(name: "Product", schema: "catalog", newName: "Product", newSchema: "ecom");
        }
    }
}
