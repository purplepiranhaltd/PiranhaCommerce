using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingAndTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShippingMethodName",
                schema: "ecom",
                table: "Order",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShippingTotal",
                schema: "ecom",
                table: "Order",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxName",
                schema: "ecom",
                table: "Order",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                schema: "ecom",
                table: "Order",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxTotal",
                schema: "ecom",
                table: "Order",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ShippingMethod",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    FreeOver = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingMethod", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxRule",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRule", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRule_CountryCode",
                schema: "ecom",
                table: "TaxRule",
                column: "CountryCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShippingMethod",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "TaxRule",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "ShippingMethodName",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "ShippingTotal",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "TaxName",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "TaxTotal",
                schema: "ecom",
                table: "Order");
        }
    }
}
