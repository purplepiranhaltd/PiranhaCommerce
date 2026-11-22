using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDisputeFeeAndEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvidenceSubmittedUtc",
                schema: "ecom",
                table: "PaymentDispute",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Fee",
                schema: "ecom",
                table: "PaymentDispute",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvidenceSubmittedUtc",
                schema: "ecom",
                table: "PaymentDispute");

            migrationBuilder.DropColumn(
                name: "Fee",
                schema: "ecom",
                table: "PaymentDispute");
        }
    }
}
