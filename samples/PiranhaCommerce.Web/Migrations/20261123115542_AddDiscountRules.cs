using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscountRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscountRule",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    BuyQuantity = table.Column<int>(type: "int", nullable: true),
                    GetQuantity = table.Column<int>(type: "int", nullable: true),
                    AllProducts = table.Column<bool>(type: "bit", nullable: false),
                    MinSubtotal = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    StartsUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndsUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Stacking = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MaxRedemptions = table.Column<int>(type: "int", nullable: true),
                    Redeemed = table.Column<int>(type: "int", nullable: false),
                    MaxPerCustomer = table.Column<int>(type: "int", nullable: true),
                    CustomerGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountRule_CustomerGroup_CustomerGroupId",
                        column: x => x.CustomerGroupId,
                        principalSchema: "customer",
                        principalTable: "CustomerGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderDiscount",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    FreeShipping = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderDiscount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderDiscount_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "ecom",
                        principalTable: "Order",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscountRuleCategory",
                schema: "ecom",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountRuleCategory", x => new { x.RuleId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_DiscountRuleCategory_Category_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Category",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscountRuleCategory_DiscountRule_RuleId",
                        column: x => x.RuleId,
                        principalSchema: "ecom",
                        principalTable: "DiscountRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscountRuleProduct",
                schema: "ecom",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountRuleProduct", x => new { x.RuleId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_DiscountRuleProduct_DiscountRule_RuleId",
                        column: x => x.RuleId,
                        principalSchema: "ecom",
                        principalTable: "DiscountRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscountRuleProduct_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRule_Code",
                schema: "ecom",
                table: "DiscountRule",
                column: "Code",
                unique: true,
                filter: "[Code] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRule_CustomerGroupId",
                schema: "ecom",
                table: "DiscountRule",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRuleCategory_CategoryId",
                schema: "ecom",
                table: "DiscountRuleCategory",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountRuleProduct_ProductId",
                schema: "ecom",
                table: "DiscountRuleProduct",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderDiscount_OrderId",
                schema: "ecom",
                table: "OrderDiscount",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderDiscount_RuleId",
                schema: "ecom",
                table: "OrderDiscount",
                column: "RuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscountRuleCategory",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "DiscountRuleProduct",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "OrderDiscount",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "DiscountRule",
                schema: "ecom");
        }
    }
}
