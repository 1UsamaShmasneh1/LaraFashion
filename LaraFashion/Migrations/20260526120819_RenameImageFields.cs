using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class RenameImageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ImageBase64",
                table: "Products",
                newName: "ImageUrl");

            migrationBuilder.RenameColumn(
                name: "ProductImageBase64",
                table: "OrderItems",
                newName: "ProductImageUrl");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ImageUrl",
                table: "Products",
                newName: "ImageBase64");

            migrationBuilder.RenameColumn(
                name: "ProductImageUrl",
                table: "OrderItems",
                newName: "ProductImageBase64");
        }
    }
}
