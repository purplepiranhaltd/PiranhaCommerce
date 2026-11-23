using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepliedBy",
                schema: "ecom",
                table: "ProductReview",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RepliedUtc",
                schema: "ecom",
                table: "ProductReview",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reply",
                schema: "ecom",
                table: "ProductReview",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepliedBy",
                schema: "ecom",
                table: "ProductReview");

            migrationBuilder.DropColumn(
                name: "RepliedUtc",
                schema: "ecom",
                table: "ProductReview");

            migrationBuilder.DropColumn(
                name: "Reply",
                schema: "ecom",
                table: "ProductReview");
        }
    }
}
