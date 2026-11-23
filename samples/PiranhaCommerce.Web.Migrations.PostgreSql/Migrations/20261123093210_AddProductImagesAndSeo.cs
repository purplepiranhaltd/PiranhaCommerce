using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProductImagesAndSeo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetaDescription",
                schema: "catalog",
                table: "Product",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaTitle",
                schema: "catalog",
                table: "Product",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                schema: "catalog",
                table: "Product",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaDescription",
                schema: "catalog",
                table: "Category",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaTitle",
                schema: "catalog",
                table: "Category",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                schema: "catalog",
                table: "Category",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductImage",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    AltText = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Sizes = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductImage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductImage_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Product_Slug",
                schema: "catalog",
                table: "Product",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_Slug",
                schema: "catalog",
                table: "Category",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImage_ProductId_Position",
                schema: "catalog",
                table: "ProductImage",
                columns: new[] { "ProductId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductImage",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_Product_Slug",
                schema: "catalog",
                table: "Product");

            migrationBuilder.DropIndex(
                name: "IX_Category_Slug",
                schema: "catalog",
                table: "Category");

            migrationBuilder.DropColumn(
                name: "MetaDescription",
                schema: "catalog",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "MetaTitle",
                schema: "catalog",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "Slug",
                schema: "catalog",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "MetaDescription",
                schema: "catalog",
                table: "Category");

            migrationBuilder.DropColumn(
                name: "MetaTitle",
                schema: "catalog",
                table: "Category");

            migrationBuilder.DropColumn(
                name: "Slug",
                schema: "catalog",
                table: "Category");
        }
    }
}
