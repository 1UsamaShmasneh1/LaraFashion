using System;
using Microsoft.EntityFrameworkCore.Migrations;
using LaraFashion.Services;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class AddStableProductSizeIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                CREATE TEMP TABLE SizeIdentityPreflight (Failure INTEGER CONSTRAINT InvalidOrAmbiguousProductSizes CHECK (Failure = 0));
                INSERT INTO SizeIdentityPreflight SELECT 1 WHERE
                  EXISTS (SELECT 1 FROM ProductSizes WHERE NOT ({SizeNamePolicy.SqlValid("SizeName")}) OR Quantity < 0 OR Quantity > 2147483647 OR typeof(Quantity) <> 'integer'
                    OR NOT ({SizeNamePolicy.SqlGuidValid("Id")}) OR NOT ({SizeNamePolicy.SqlGuidValid("ProductId")})
                    OR NOT EXISTS (SELECT 1 FROM Products WHERE Products.Id = ProductSizes.ProductId))
                  OR EXISTS (SELECT 1 FROM ProductSizes GROUP BY ProductId, {SizeNamePolicy.SqlKey("SizeName")} HAVING count(*) > 1);
                DROP TABLE SizeIdentityPreflight;
                """);
            migrationBuilder.DropIndex(
                name: "IX_ProductSizes_ProductId",
                table: "ProductSizes");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "ProductSizes",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                collation: "BINARY");

            migrationBuilder.AddColumn<Guid>(
                name: "ProductSizeId",
                table: "OrderItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql($"UPDATE ProductSizes SET NormalizedName = {SizeNamePolicy.SqlKey("SizeName")};");
            migrationBuilder.CreateIndex(
                name: "IX_ProductSizes_ProductId_NormalizedName",
                table: "ProductSizes",
                columns: new[] { "ProductId", "NormalizedName" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE TABLE ProductSizeIdentities (Id TEXT NOT NULL PRIMARY KEY, ProductId TEXT NOT NULL);
                INSERT INTO ProductSizeIdentities SELECT Id, ProductId FROM ProductSizes;
                CREATE TRIGGER ProductSizeIdentityBeforeInsert BEFORE INSERT ON ProductSizes
                  WHEN EXISTS (SELECT 1 FROM ProductSizeIdentities WHERE Id = NEW.Id)
                  BEGIN SELECT RAISE(ABORT, 'Product size identity already used'); END;
                CREATE TRIGGER ProductSizeIdentityAfterInsert AFTER INSERT ON ProductSizes
                  BEGIN INSERT INTO ProductSizeIdentities VALUES (NEW.Id, NEW.ProductId); END;
                CREATE TRIGGER ProductSizeIdentityImmutable BEFORE UPDATE OF Id, ProductId ON ProductSizes
                  WHEN NEW.Id <> OLD.Id OR NEW.ProductId <> OLD.ProductId
                  BEGIN SELECT RAISE(ABORT, 'Product size identity immutable'); END;
                CREATE TRIGGER ProductSizeLedgerNoUpdate BEFORE UPDATE ON ProductSizeIdentities
                  BEGIN SELECT RAISE(ABORT, 'Product size identity ledger immutable'); END;
                CREATE TRIGGER ProductSizeLedgerNoDelete BEFORE DELETE ON ProductSizeIdentities
                  BEGIN SELECT RAISE(ABORT, 'Product size identity ledger retained'); END;
                """);
            foreach (var operation in new[] { "INSERT", "UPDATE" })
                migrationBuilder.Sql($"""
                    CREATE TRIGGER ProductSizeValidBefore{operation} BEFORE {operation} ON ProductSizes
                    WHEN NOT ({SizeNamePolicy.SqlValid("NEW.SizeName")}) OR NEW.NormalizedName <> {SizeNamePolicy.SqlKey("NEW.SizeName")}
                      OR NOT ({SizeNamePolicy.SqlGuidValid("NEW.Id")}) OR NOT ({SizeNamePolicy.SqlGuidValid("NEW.ProductId")})
                      OR typeof(NEW.Quantity) <> 'integer' OR NEW.Quantity < 0 OR NEW.Quantity > 2147483647
                    BEGIN SELECT RAISE(ABORT, 'Invalid product size key or quantity'); END;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER ProductSizeIdentityBeforeInsert;
                DROP TRIGGER ProductSizeIdentityAfterInsert;
                DROP TRIGGER ProductSizeIdentityImmutable;
                DROP TRIGGER ProductSizeLedgerNoUpdate;
                DROP TRIGGER ProductSizeLedgerNoDelete;
                DROP TRIGGER ProductSizeValidBeforeINSERT;
                DROP TRIGGER ProductSizeValidBeforeUPDATE;
                DROP TABLE ProductSizeIdentities;
                """);
            migrationBuilder.DropIndex(
                name: "IX_ProductSizes_ProductId_NormalizedName",
                table: "ProductSizes");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "ProductSizes");

            migrationBuilder.DropColumn(
                name: "ProductSizeId",
                table: "OrderItems");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSizes_ProductId",
                table: "ProductSizes",
                column: "ProductId");
        }
    }
}
