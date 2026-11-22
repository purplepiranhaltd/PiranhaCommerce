using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddStockTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffLocation",
                schema: "catalog",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffLocation", x => new { x.UserId, x.LocationId });
                    table.ForeignKey(
                        name: "FK_StaffLocation_StockLocation_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "catalog",
                        principalTable: "StockLocation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffLocation_User_UserId",
                        column: x => x.UserId,
                        principalSchema: "core",
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfer",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SentUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SentByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReceivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HasDiscrepancy = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockTransfer_StockLocation_FromLocationId",
                        column: x => x.FromLocationId,
                        principalSchema: "catalog",
                        principalTable: "StockLocation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfer_StockLocation_ToLocationId",
                        column: x => x.ToLocationId,
                        principalSchema: "catalog",
                        principalTable: "StockLocation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferCounter",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferCounter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLine",
                schema: "catalog",
                columns: table => new
                {
                    TransferId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    QuantitySent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLine", x => new { x.TransferId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_StockTransferLine_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLine_StockTransfer_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "catalog",
                        principalTable: "StockTransfer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "StockTransferCounter",
                columns: new[] { "Id", "LastNumber" },
                values: new object[] { 1, 1000L });

            migrationBuilder.CreateIndex(
                name: "IX_StaffLocation_LocationId",
                schema: "catalog",
                table: "StaffLocation",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_FromLocationId",
                schema: "catalog",
                table: "StockTransfer",
                column: "FromLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_Number",
                schema: "catalog",
                table: "StockTransfer",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_Status_Number",
                schema: "catalog",
                table: "StockTransfer",
                columns: new[] { "Status", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfer_ToLocationId",
                schema: "catalog",
                table: "StockTransfer",
                column: "ToLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLine_ProductId",
                schema: "catalog",
                table: "StockTransferLine",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffLocation",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "StockTransferCounter",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "StockTransferLine",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "StockTransfer",
                schema: "catalog");
        }
    }
}
