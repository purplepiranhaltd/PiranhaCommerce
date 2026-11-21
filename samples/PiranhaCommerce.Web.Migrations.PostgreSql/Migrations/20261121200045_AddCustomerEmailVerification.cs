using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerEmailVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailVerifiedUtc",
                schema: "customer",
                table: "CustomerLogin",
                type: "timestamp with time zone",
                nullable: true);

            // Sign-ins that exist now keep working if a shop later requires verified emails: they count as verified from when they were made.
            migrationBuilder.Sql("UPDATE customer.\"CustomerLogin\" SET \"EmailVerifiedUtc\" = \"CreatedUtc\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailVerifiedUtc",
                schema: "customer",
                table: "CustomerLogin");
        }
    }
}
