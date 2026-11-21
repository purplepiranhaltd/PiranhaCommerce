using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentDispute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentDispute",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OpenedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondByUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentDispute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentDispute_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ecom",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentDispute_OrderId_OpenedUtc",
                schema: "ecom",
                table: "PaymentDispute",
                columns: new[] { "OrderId", "OpenedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentDispute_Provider_Reference",
                schema: "ecom",
                table: "PaymentDispute",
                columns: new[] { "Provider", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentDispute_Status_OpenedUtc",
                schema: "ecom",
                table: "PaymentDispute",
                columns: new[] { "Status", "OpenedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentDispute",
                schema: "ecom");
        }
    }
}
