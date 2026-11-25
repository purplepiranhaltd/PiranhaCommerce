using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
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
                type: "datetimeoffset",
                nullable: true);

            // Sign-ins that exist now keep working if a shop later requires verified emails: they count as verified from when they were made.
            // EXEC: in a script for a whole database this statement is in the same batch as the change it depends on, and SQL Server checks column names before running any of it.
            migrationBuilder.Sql("EXEC(N'UPDATE [customer].[CustomerLogin] SET [EmailVerifiedUtc] = [CreatedUtc]')");
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
