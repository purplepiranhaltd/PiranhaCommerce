using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxClasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaxRule_CountryCode",
                schema: "ecom",
                table: "TaxRule");

            migrationBuilder.AddColumn<Guid>(
                name: "TaxClassId",
                schema: "ecom",
                table: "TaxRule",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-0000000000a1"));

            migrationBuilder.AddColumn<Guid>(
                name: "TaxClassId",
                schema: "ecom",
                table: "ProductListing",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                schema: "ecom",
                table: "OrderLine",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TaxName",
                schema: "ecom",
                table: "OrderLine",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxRate",
                schema: "ecom",
                table: "OrderLine",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ShippingTax",
                schema: "ecom",
                table: "Order",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ShippingTaxName",
                schema: "ecom",
                table: "Order",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShippingTaxRate",
                schema: "ecom",
                table: "Order",
                type: "decimal(9,4)",
                precision: 9,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TaxClass",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxClass", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "ecom",
                table: "TaxClass",
                columns: new[] { "Id", "CreatedUtc", "IsDefault", "Name", "UpdatedUtc" },
                values: new object[] { new Guid("00000000-0000-0000-0000-0000000000a1"), new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Standard", null });

            migrationBuilder.CreateIndex(
                name: "IX_TaxRule_TaxClassId_CountryCode",
                schema: "ecom",
                table: "TaxRule",
                columns: new[] { "TaxClassId", "CountryCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductListing_TaxClassId",
                schema: "ecom",
                table: "ProductListing",
                column: "TaxClassId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxClass_Name",
                schema: "ecom",
                table: "TaxClass",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductListing_TaxClass_TaxClassId",
                schema: "ecom",
                table: "ProductListing",
                column: "TaxClassId",
                principalSchema: "ecom",
                principalTable: "TaxClass",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TaxRule_TaxClass_TaxClassId",
                schema: "ecom",
                table: "TaxRule",
                column: "TaxClassId",
                principalSchema: "ecom",
                principalTable: "TaxClass",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductListing_TaxClass_TaxClassId",
                schema: "ecom",
                table: "ProductListing");

            migrationBuilder.DropForeignKey(
                name: "FK_TaxRule_TaxClass_TaxClassId",
                schema: "ecom",
                table: "TaxRule");

            migrationBuilder.DropTable(
                name: "TaxClass",
                schema: "ecom");

            migrationBuilder.DropIndex(
                name: "IX_TaxRule_TaxClassId_CountryCode",
                schema: "ecom",
                table: "TaxRule");

            migrationBuilder.DropIndex(
                name: "IX_ProductListing_TaxClassId",
                schema: "ecom",
                table: "ProductListing");

            migrationBuilder.DropColumn(
                name: "TaxClassId",
                schema: "ecom",
                table: "TaxRule");

            migrationBuilder.DropColumn(
                name: "TaxClassId",
                schema: "ecom",
                table: "ProductListing");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                schema: "ecom",
                table: "OrderLine");

            migrationBuilder.DropColumn(
                name: "TaxName",
                schema: "ecom",
                table: "OrderLine");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                schema: "ecom",
                table: "OrderLine");

            migrationBuilder.DropColumn(
                name: "ShippingTax",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "ShippingTaxName",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "ShippingTaxRate",
                schema: "ecom",
                table: "Order");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRule_CountryCode",
                schema: "ecom",
                table: "TaxRule",
                column: "CountryCode",
                unique: true);
        }
    }
}
