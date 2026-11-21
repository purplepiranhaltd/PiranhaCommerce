using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingRatesAndWeight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WeightGrams",
                schema: "ecom",
                table: "ProductListing",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShippingRate",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShippingMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    MinWeightGrams = table.Column<int>(type: "int", nullable: false),
                    MaxWeightGrams = table.Column<int>(type: "int", nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingRate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShippingRate_ShippingMethod_ShippingMethodId",
                        column: x => x.ShippingMethodId,
                        principalSchema: "ecom",
                        principalTable: "ShippingMethod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShippingRate_ShippingMethodId_CountryCode",
                schema: "ecom",
                table: "ShippingRate",
                columns: new[] { "ShippingMethodId", "CountryCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShippingRate",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "WeightGrams",
                schema: "ecom",
                table: "ProductListing");
        }
    }
}
