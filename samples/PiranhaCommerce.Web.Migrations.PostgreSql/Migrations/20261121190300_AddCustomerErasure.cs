using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErasedUtc",
                schema: "ecom",
                table: "Order",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerErasure",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErasedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerErasure", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerErasure_CustomerId",
                schema: "customer",
                table: "CustomerErasure",
                column: "CustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerErasure",
                schema: "customer");

            migrationBuilder.DropColumn(
                name: "ErasedUtc",
                schema: "ecom",
                table: "Order");
        }
    }
}
