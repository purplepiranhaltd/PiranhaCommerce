using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFulfilmentAndReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuantityReturned",
                schema: "ecom",
                table: "OrderLine",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "QuantityShipped",
                schema: "ecom",
                table: "OrderLine",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Orders completed before shipments existed were sent whole: say so, or nothing on them could ever be returned.
            migrationBuilder.Sql(@"UPDATE ol SET ol.QuantityShipped = ol.Quantity FROM [ecom].[OrderLine] ol INNER JOIN [ecom].[Order] o ON o.Id = ol.OrderId WHERE o.Stage = 'Completed'");

            migrationBuilder.CreateTable(
                name: "OrderReturn",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuggestedRefund = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ShippingIncluded = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReturn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderReturn_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ecom",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shipment",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShippedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Carrier = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TrackingNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TrackingUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shipment_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ecom",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderReturnLine",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Restocked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReturnLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderReturnLine_OrderLine_OrderLineId",
                        column: x => x.OrderLineId,
                        principalSchema: "ecom",
                        principalTable: "OrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderReturnLine_OrderReturn_OrderReturnId",
                        column: x => x.OrderReturnId,
                        principalSchema: "ecom",
                        principalTable: "OrderReturn",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentLine",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_OrderLine_OrderLineId",
                        column: x => x.OrderLineId,
                        principalSchema: "ecom",
                        principalTable: "OrderLine",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLine_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalSchema: "ecom",
                        principalTable: "Shipment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderReturn_OrderId_CreatedUtc",
                schema: "ecom",
                table: "OrderReturn",
                columns: new[] { "OrderId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderReturnLine_OrderLineId",
                schema: "ecom",
                table: "OrderReturnLine",
                column: "OrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderReturnLine_OrderReturnId",
                schema: "ecom",
                table: "OrderReturnLine",
                column: "OrderReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_OrderId_ShippedUtc",
                schema: "ecom",
                table: "Shipment",
                columns: new[] { "OrderId", "ShippedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_OrderLineId",
                schema: "ecom",
                table: "ShipmentLine",
                column: "OrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLine_ShipmentId",
                schema: "ecom",
                table: "ShipmentLine",
                column: "ShipmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderReturnLine",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "ShipmentLine",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "OrderReturn",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "Shipment",
                schema: "ecom");

            migrationBuilder.DropColumn(
                name: "QuantityReturned",
                schema: "ecom",
                table: "OrderLine");

            migrationBuilder.DropColumn(
                name: "QuantityShipped",
                schema: "ecom",
                table: "OrderLine");
        }
    }
}
