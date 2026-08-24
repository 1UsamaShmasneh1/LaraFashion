using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeProductImageGuidFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TRIGGER "TR_ProductImages_NormalizeGuid_NoCollision"
                BEFORE UPDATE OF "Id" ON "ProductImages"
                WHEN EXISTS (
                    SELECT 1
                    FROM "ProductImages" AS "existing"
                    WHERE "existing"."Id" <> OLD."Id"
                      AND lower("existing"."Id") =
                          lower(
                              substr(OLD."Id", 1, 8) || '-' ||
                              substr(OLD."Id", 9, 4) || '-' ||
                              substr(OLD."Id", 13, 4) || '-' ||
                              substr(OLD."Id", 17, 4) || '-' ||
                              substr(OLD."Id", 21, 12)
                          )
                )
                BEGIN
                    SELECT RAISE(ABORT, 'ProductImages.Id GUID normalization collision');
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "ProductImages"
                SET "Id" =
                    upper(
                        substr("Id", 1, 8) || '-' ||
                        substr("Id", 9, 4) || '-' ||
                        substr("Id", 13, 4) || '-' ||
                        substr("Id", 17, 4) || '-' ||
                        substr("Id", 21, 12)
                    )
                WHERE length("Id") = 32
                  AND "Id" NOT GLOB '*[^0-9A-Fa-f]*';
                """);

            migrationBuilder.Sql(
                """DROP TRIGGER "TR_ProductImages_NormalizeGuid_NoCollision";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: returning to the legacy undashed format
            // would reintroduce the EF Core key-comparison failure fixed by Up().
        }
    }
}
