using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddUnreconciledPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnreconciledPayment",
                schema: "ecom",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnreconciledPayment", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnreconciledPayment_Provider_Reference",
                schema: "ecom",
                table: "UnreconciledPayment",
                columns: new[] { "Provider", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnreconciledPayment_ResolvedUtc_CreatedUtc",
                schema: "ecom",
                table: "UnreconciledPayment",
                columns: new[] { "ResolvedUtc", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnreconciledPayment",
                schema: "ecom");
        }
    }
}
