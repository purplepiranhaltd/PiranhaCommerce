using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpecAttribute",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    Filterable = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecAttribute", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpecValue",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecValue_SpecAttribute_AttributeId",
                        column: x => x.AttributeId,
                        principalSchema: "catalog",
                        principalTable: "SpecAttribute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductSpecValue",
                schema: "catalog",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSpecValue", x => new { x.ProductId, x.ValueId });
                    table.ForeignKey(
                        name: "FK_ProductSpecValue_Product_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductSpecValue_SpecValue_ValueId",
                        column: x => x.ValueId,
                        principalSchema: "catalog",
                        principalTable: "SpecValue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductSpecValue_ValueId",
                schema: "catalog",
                table: "ProductSpecValue",
                column: "ValueId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecAttribute_NormalizedName",
                schema: "catalog",
                table: "SpecAttribute",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpecAttribute_Slug",
                schema: "catalog",
                table: "SpecAttribute",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpecValue_AttributeId_NormalizedValue",
                schema: "catalog",
                table: "SpecValue",
                columns: new[] { "AttributeId", "NormalizedValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpecValue_AttributeId_Slug",
                schema: "catalog",
                table: "SpecValue",
                columns: new[] { "AttributeId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductSpecValue",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SpecValue",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SpecAttribute",
                schema: "catalog");
        }
    }
}
