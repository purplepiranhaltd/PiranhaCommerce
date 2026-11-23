using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddStockAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockAlertSubscription",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnsubscribeToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NotifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAlertSubscription", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockAlertSubscription_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockAlertSubscription_NormalizedEmail",
                schema: "ecom",
                table: "StockAlertSubscription",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_StockAlertSubscription_NotifiedUtc_ProductId",
                schema: "ecom",
                table: "StockAlertSubscription",
                columns: new[] { "NotifiedUtc", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAlertSubscription_ProductId_NormalizedEmail",
                schema: "ecom",
                table: "StockAlertSubscription",
                columns: new[] { "ProductId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAlertSubscription_UnsubscribeToken",
                schema: "ecom",
                table: "StockAlertSubscription",
                column: "UnsubscribeToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockAlertSubscription",
                schema: "ecom");
        }
    }
}
