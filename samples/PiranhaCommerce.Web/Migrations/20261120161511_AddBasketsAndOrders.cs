using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddBasketsAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Basket",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Basket", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Order",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ShippingRecipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShippingLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShippingLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ShippingCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShippingRegion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ShippingPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ShippingCountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    BillingRecipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingRegion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BillingCountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderCounter",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCounter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BasketLine",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BasketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BasketLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BasketLine_Basket_BasketId",
                        column: x => x.BasketId,
                        principalSchema: "ecom",
                        principalTable: "Basket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BasketLine_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderLine",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderLine_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ecom",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderLine_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ecom",
                table: "OrderCounter",
                columns: new[] { "Id", "LastNumber" },
                values: new object[] { 1, 1000L });

            migrationBuilder.CreateIndex(
                name: "IX_BasketLine_BasketId_ProductId",
                schema: "ecom",
                table: "BasketLine",
                columns: new[] { "BasketId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BasketLine_ProductId",
                schema: "ecom",
                table: "BasketLine",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Order_Email",
                schema: "ecom",
                table: "Order",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Order_Number",
                schema: "ecom",
                table: "Order",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_Stage_Number",
                schema: "ecom",
                table: "Order",
                columns: new[] { "Stage", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLine_OrderId_Position",
                schema: "ecom",
                table: "OrderLine",
                columns: new[] { "OrderId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLine_ProductId",
                schema: "ecom",
                table: "OrderLine",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BasketLine",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "OrderCounter",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "OrderLine",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "Basket",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "Order",
                schema: "ecom");
        }
    }
}
