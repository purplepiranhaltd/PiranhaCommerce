using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerGroupId",
                schema: "ecom",
                table: "Promotion",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerGroup",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerGroupMember",
                schema: "customer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AddedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroupMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerGroupMember_CustomerGroup_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "customer",
                        principalTable: "CustomerGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerGroupMember_Customer_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "customer",
                        principalTable: "Customer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Promotion_CustomerGroupId",
                schema: "ecom",
                table: "Promotion",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroup_NormalizedName",
                schema: "customer",
                table: "CustomerGroup",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroupMember_CustomerId",
                schema: "customer",
                table: "CustomerGroupMember",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroupMember_GroupId_CustomerId",
                schema: "customer",
                table: "CustomerGroupMember",
                columns: new[] { "GroupId", "CustomerId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Promotion_CustomerGroup_CustomerGroupId",
                schema: "ecom",
                table: "Promotion",
                column: "CustomerGroupId",
                principalSchema: "customer",
                principalTable: "CustomerGroup",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Promotion_CustomerGroup_CustomerGroupId",
                schema: "ecom",
                table: "Promotion");

            migrationBuilder.DropTable(
                name: "CustomerGroupMember",
                schema: "customer");

            migrationBuilder.DropTable(
                name: "CustomerGroup",
                schema: "customer");

            migrationBuilder.DropIndex(
                name: "IX_Promotion_CustomerGroupId",
                schema: "ecom",
                table: "Promotion");

            migrationBuilder.DropColumn(
                name: "CustomerGroupId",
                schema: "ecom",
                table: "Promotion");
        }
    }
}
