using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiranhaCommerce.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerSessionVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionVersion",
                schema: "customer",
                table: "CustomerLogin",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionVersion",
                schema: "customer",
                table: "CustomerLogin");
        }
    }
}
