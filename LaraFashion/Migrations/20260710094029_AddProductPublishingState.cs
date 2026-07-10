using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class AddProductPublishingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublished",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublished",
                table: "Products");
        }
    }
}
