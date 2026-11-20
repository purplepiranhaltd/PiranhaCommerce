using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_StockLevel",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.AddColumn<decimal>(
                name: "Balance",
                schema: "catalog",
                table: "StockMovement",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            // Existing rows belong to the built-in default location (inserted below, before the foreign keys are added).
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                schema: "catalog",
                table: "StockMovement",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                schema: "catalog",
                table: "StockLevel",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockLevel",
                schema: "catalog",
                table: "StockLevel",
                columns: new[] { "ProductId", "LocationId" });

            migrationBuilder.CreateTable(
                name: "StockLocation",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLocation", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "StockLocation",
                columns: new[] { "Id", "Active", "Code", "Name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), true, "MAIN", "Main" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovement_LocationId",
                schema: "catalog",
                table: "StockMovement",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevel_LocationId",
                schema: "catalog",
                table: "StockLevel",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevel_Quantity",
                schema: "catalog",
                table: "StockLevel",
                column: "Quantity");

            migrationBuilder.CreateIndex(
                name: "IX_StockLocation_Code",
                schema: "catalog",
                table: "StockLocation",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_StockLevel_StockLocation_LocationId",
                schema: "catalog",
                table: "StockLevel",
                column: "LocationId",
                principalSchema: "catalog",
                principalTable: "StockLocation",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovement_StockLocation_LocationId",
                schema: "catalog",
                table: "StockMovement",
                column: "LocationId",
                principalSchema: "catalog",
                principalTable: "StockLocation",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockLevel_StockLocation_LocationId",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.DropForeignKey(
                name: "FK_StockMovement_StockLocation_LocationId",
                schema: "catalog",
                table: "StockMovement");

            migrationBuilder.DropTable(
                name: "StockLocation",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_StockMovement_LocationId",
                schema: "catalog",
                table: "StockMovement");

            migrationBuilder.DropPrimaryKey(
                name: "PK_StockLevel",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.DropIndex(
                name: "IX_StockLevel_LocationId",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.DropIndex(
                name: "IX_StockLevel_Quantity",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.DropColumn(
                name: "Balance",
                schema: "catalog",
                table: "StockMovement");

            migrationBuilder.DropColumn(
                name: "LocationId",
                schema: "catalog",
                table: "StockMovement");

            migrationBuilder.DropColumn(
                name: "LocationId",
                schema: "catalog",
                table: "StockLevel");

            migrationBuilder.AddPrimaryKey(
                name: "PK_StockLevel",
                schema: "catalog",
                table: "StockLevel",
                column: "ProductId");
        }
    }
}
