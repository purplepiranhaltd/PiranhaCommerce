using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingZonesAndCarriers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CountryCode",
                schema: "ecom",
                table: "ShippingRate",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                schema: "ecom",
                table: "ShippingRate",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShippingCarrier",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TrackingUrlTemplate = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingCarrier", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShippingZone",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingZone", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShippingZoneRegion",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingZoneRegion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShippingZoneRegion_ShippingZone_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "ecom",
                        principalTable: "ShippingZone",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShippingRate_ZoneId",
                schema: "ecom",
                table: "ShippingRate",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_ShippingZoneRegion_CountryCode_Prefix",
                schema: "ecom",
                table: "ShippingZoneRegion",
                columns: new[] { "CountryCode", "Prefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShippingZoneRegion_ZoneId",
                schema: "ecom",
                table: "ShippingZoneRegion",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_ShippingRate_ShippingZone_ZoneId",
                schema: "ecom",
                table: "ShippingRate",
                column: "ZoneId",
                principalSchema: "ecom",
                principalTable: "ShippingZone",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ShippingRate_ShippingZone_ZoneId",
                schema: "ecom",
                table: "ShippingRate");

            migrationBuilder.DropTable(
                name: "ShippingCarrier",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "ShippingZoneRegion",
                schema: "ecom");

            migrationBuilder.DropTable(
                name: "ShippingZone",
                schema: "ecom");

            migrationBuilder.DropIndex(
                name: "IX_ShippingRate_ZoneId",
                schema: "ecom",
                table: "ShippingRate");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                schema: "ecom",
                table: "ShippingRate");

            migrationBuilder.AlterColumn<string>(
                name: "CountryCode",
                schema: "ecom",
                table: "ShippingRate",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldNullable: true);
        }
    }
}
