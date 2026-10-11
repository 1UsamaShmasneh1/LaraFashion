using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalOrderInventoryReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(name: "ReviewRevision", table: "ProductSizes", type: "INTEGER", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<long>(
                name: "InventoryRevision",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "HistoricalOrderItemEvidence",
                columns: table => new
                {
                    OrderItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SourceMigration = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalOrderItemEvidence", x => x.OrderItemId);
                });

            migrationBuilder.CreateTable(
                name: "OrderInventoryReviews",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderNumber = table.Column<string>(type: "TEXT", nullable: false),
                    IsSandbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreviousStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    NewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    NewRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AdminIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderInventoryReviews", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "OrderInventoryReviewLines",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HistoricalSize = table.Column<string>(type: "TEXT", nullable: false),
                    IsHistorical = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetSizeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSizeName = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSizeKey = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderInventoryReviewLines", x => new { x.OperationId, x.OrderItemId });
                    table.ForeignKey(
                        name: "FK_OrderInventoryReviewLines_OrderInventoryReviews_OperationId",
                        column: x => x.OperationId,
                        principalTable: "OrderInventoryReviews",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderInventoryReviewMovements",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSizeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSizeName = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    StockBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    StockAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetRevisionBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetRevisionAfter = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderInventoryReviewMovements", x => new { x.OperationId, x.TargetSizeId });
                    table.ForeignKey(
                        name: "FK_OrderInventoryReviewMovements_OrderInventoryReviews_OperationId",
                        column: x => x.OperationId,
                        principalTable: "OrderInventoryReviews",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalOrderItemEvidence_OrderId",
                table: "HistoricalOrderItemEvidence",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderInventoryReviews_OrderId_NewRevision",
                table: "OrderInventoryReviews",
                columns: new[] { "OrderId", "NewRevision" },
                unique: true);
            // The offline preparer accepts only a hash-verified pre-identity source.
            // An unprepared/already-upgraded database remains ineligible, never NULL => legacy.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS LegacyOrderItemEvidenceStaging (
                  OrderItemId TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, ProductId TEXT NOT NULL,
                  ItemFingerprint TEXT NOT NULL, SourceSha256 TEXT NOT NULL, SourceMigration TEXT NOT NULL,
                  RegisteredAtUtc TEXT NOT NULL);
                INSERT INTO HistoricalOrderItemEvidence
                  SELECT e.* FROM LegacyOrderItemEvidenceStaging e JOIN OrderItems i ON i.Id = e.OrderItemId
                    AND i.OrderId = e.OrderId AND i.ProductId = e.ProductId WHERE i.ProductSizeId IS NULL;
                DROP TABLE LegacyOrderItemEvidenceStaging;
                CREATE TRIGGER HistoricalOrderItemEvidenceNoINSERT BEFORE INSERT ON HistoricalOrderItemEvidence
                  BEGIN SELECT RAISE(ABORT, 'Historical eligibility boundary closed after migration'); END;
                CREATE TRIGGER HistoricalOrderItemNoReinsert BEFORE INSERT ON OrderItems
                  WHEN EXISTS (SELECT 1 FROM HistoricalOrderItemEvidence WHERE OrderItemId = NEW.Id)
                  BEGIN SELECT RAISE(ABORT, 'Historical order item identity already registered'); END;
                CREATE TRIGGER OrderInventoryRevisionGuard BEFORE UPDATE ON Orders
                  WHEN typeof(NEW.InventoryRevision) <> 'integer' OR NEW.InventoryRevision < 0
                    OR (NEW.Status <> OLD.Status AND NEW.InventoryRevision <> OLD.InventoryRevision + 1)
                    OR (NEW.Status = OLD.Status AND NEW.InventoryRevision <> OLD.InventoryRevision)
                  BEGIN SELECT RAISE(ABORT, 'Invalid order inventory revision'); END;
                CREATE TRIGGER ProductSizeReviewRevisionGuard BEFORE UPDATE OF ReviewRevision ON ProductSizes
                  WHEN typeof(NEW.ReviewRevision) <> 'integer' OR NEW.ReviewRevision < 0 OR NEW.ReviewRevision <> OLD.ReviewRevision + 1
                  BEGIN SELECT RAISE(ABORT, 'Invalid size review revision'); END;
                CREATE TRIGGER ProductSizeReviewRevisionInsertGuard BEFORE INSERT ON ProductSizes
                  WHEN typeof(NEW.ReviewRevision) <> 'integer' OR NEW.ReviewRevision <> 0
                  BEGIN SELECT RAISE(ABORT, 'Invalid initial size review revision'); END;
                CREATE TRIGGER ProductSizeReviewRevisionAdvance AFTER UPDATE OF Quantity, SizeName, NormalizedName ON ProductSizes
                  WHEN NEW.Quantity <> OLD.Quantity OR NEW.SizeName <> OLD.SizeName OR NEW.NormalizedName <> OLD.NormalizedName
                  BEGIN UPDATE ProductSizes SET ReviewRevision = OLD.ReviewRevision + 1 WHERE Id = NEW.Id; END;
                """);
            foreach (var table in new[] { "HistoricalOrderItemEvidence", "OrderInventoryReviews", "OrderInventoryReviewLines", "OrderInventoryReviewMovements" })
                foreach (var operation in new[] { "UPDATE", "DELETE" })
                    migrationBuilder.Sql($"CREATE TRIGGER {table}No{operation} BEFORE {operation} ON {table} BEGIN SELECT RAISE(ABORT, 'Inventory review evidence and audit retained'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE InventoryReviewRollbackGuard (Failure INTEGER CHECK (Failure = 0));
                INSERT INTO InventoryReviewRollbackGuard SELECT 1 WHERE EXISTS (SELECT 1 FROM OrderInventoryReviews);
                DROP TABLE InventoryReviewRollbackGuard;
                DROP TRIGGER OrderInventoryRevisionGuard;
                DROP TRIGGER ProductSizeReviewRevisionGuard;
                DROP TRIGGER ProductSizeReviewRevisionInsertGuard;
                DROP TRIGGER ProductSizeReviewRevisionAdvance;
                DROP TRIGGER HistoricalOrderItemEvidenceNoINSERT;
                DROP TRIGGER HistoricalOrderItemNoReinsert;
                """);
            foreach (var table in new[] { "HistoricalOrderItemEvidence", "OrderInventoryReviews", "OrderInventoryReviewLines", "OrderInventoryReviewMovements" })
                foreach (var operation in new[] { "UPDATE", "DELETE" })
                    migrationBuilder.Sql($"DROP TRIGGER {table}No{operation};");
            migrationBuilder.DropTable(
                name: "HistoricalOrderItemEvidence");

            migrationBuilder.DropTable(
                name: "OrderInventoryReviewLines");

            migrationBuilder.DropTable(
                name: "OrderInventoryReviewMovements");

            migrationBuilder.DropTable(
                name: "OrderInventoryReviews");

            // Native SQLite DROP COLUMN preserves prior triggers. EF's table rebuild loses
            // custom triggers not represented in its model. Tested on the actual SQLite engine.
            migrationBuilder.Sql("ALTER TABLE Orders DROP COLUMN InventoryRevision; ALTER TABLE ProductSizes DROP COLUMN ReviewRevision;");
        }
    }
}
