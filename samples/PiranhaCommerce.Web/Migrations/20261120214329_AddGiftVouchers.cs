using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VoucherId",
                schema: "ecom",
                table: "Order",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VoucherPaid",
                schema: "ecom",
                table: "Order",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "VoucherId",
                schema: "ecom",
                table: "Basket",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GiftVoucher",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CodeLast4 = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    InitialValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftVoucher", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoucherEntry",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoucherEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoucherEntry_GiftVoucher_VoucherId",
                        column: x => x.VoucherId,
                        principalSchema: "ecom",
                        principalTable: "GiftVoucher",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GiftVoucher_CodeHash",
                schema: "ecom",
                table: "GiftVoucher",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoucherEntry_VoucherId_CreatedUtc",
                schema: "ecom",
                table: "VoucherEntry",
                columns: new[] { "VoucherId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoucherEntry",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "GiftVoucher",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "VoucherId",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "VoucherPaid",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "VoucherId",
                schema: "ecom",
                table: "Basket");
        }
    }
}
