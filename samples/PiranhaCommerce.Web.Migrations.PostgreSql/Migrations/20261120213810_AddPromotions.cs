using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                schema: "ecom",
                table: "OrderLine",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountTotal",
                schema: "ecom",
                table: "Order",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PromotionCode",
                schema: "ecom",
                table: "Order",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromotionId",
                schema: "ecom",
                table: "Order",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionCode",
                schema: "ecom",
                table: "Basket",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Promotion",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MinSubtotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    StartsUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: true),
                    Redeemed = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promotion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Promotion_Code",
                schema: "ecom",
                table: "Promotion",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Promotion",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                schema: "ecom",
                table: "OrderLine");

            migrationBuilder.DropColumn(
                name: "DiscountTotal",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "PromotionCode",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "PromotionId",
                schema: "ecom",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "PromotionCode",
                schema: "ecom",
                table: "Basket");
        }
    }
}
