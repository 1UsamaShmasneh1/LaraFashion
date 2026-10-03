using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations
{
    /// <inheritdoc />
    public partial class AddSafeOrderNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail closed before imposing uniqueness: never silently renumber legacy data.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE SafeNumberGuard (Value INTEGER CONSTRAINT AmbiguousOrderNumbersRequireReview CHECK (Value = 0));
                INSERT INTO SafeNumberGuard
                SELECT COUNT(*) FROM (
                    SELECT upper(trim(OrderNumber)) AS Number, Id AS Owner FROM Orders
                    UNION ALL
                    SELECT upper(trim(OrderNumber)), coalesce(OriginalOrderId, Id) FROM SalesHistory
                ) GROUP BY Number HAVING COUNT(DISTINCT Owner) > 1;
                INSERT INTO SafeNumberGuard SELECT COUNT(*) FROM (
                    SELECT OrderNumber FROM Orders UNION ALL SELECT OrderNumber FROM SalesHistory
                ) WHERE length(trim(OrderNumber)) NOT BETWEEN 1 AND 64
                    OR upper(trim(OrderNumber)) GLOB '*[^A-Z0-9-]*'
                    OR substr(upper(trim(OrderNumber)), 1, 1) NOT GLOB '[A-Z0-9]';
                INSERT INTO SafeNumberGuard SELECT COUNT(*) FROM SalesHistory h JOIN Orders o ON h.OriginalOrderId = o.Id
                    WHERE upper(trim(h.OrderNumber)) <> upper(trim(o.OrderNumber)) OR o.IsSandbox = 1;
                -- An unlinked snapshot has its own owner; an equal GUID is not proof of a link.
                INSERT INTO SafeNumberGuard SELECT COUNT(*) FROM SalesHistory h WHERE h.OriginalOrderId IS NULL
                    AND (EXISTS (SELECT 1 FROM Orders o WHERE o.Id = h.Id)
                        OR EXISTS (SELECT 1 FROM SalesHistory linked WHERE linked.OriginalOrderId = h.Id));
                DROP TABLE SafeNumberGuard;
                """);
            migrationBuilder.AddColumn<int>(
                name: "NumberRevision",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CheckoutAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderNumberAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreviousNumber = table.Column<string>(type: "TEXT", nullable: false),
                    NewNumber = table.Column<string>(type: "TEXT", nullable: false),
                    AdminIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderNumberAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderNumberReservations",
                columns: table => new
                {
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderNumberReservations", x => x.Number);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutAttempts_OrderId",
                table: "CheckoutAttempts",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderNumberAudits_OrderId",
                table: "OrderNumberAudits",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderNumberReservations_OrderId",
                table: "OrderNumberReservations",
                column: "OrderId");

            migrationBuilder.Sql("""
                INSERT INTO OrderNumberReservations (Number, OrderId)
                SELECT upper(trim(OrderNumber)), Id FROM Orders
                UNION SELECT upper(trim(OrderNumber)), coalesce(OriginalOrderId, Id) FROM SalesHistory;
                CREATE TABLE OrderIdentities (OrderId TEXT NOT NULL PRIMARY KEY);
                INSERT INTO OrderIdentities SELECT Id FROM Orders
                    UNION SELECT coalesce(OriginalOrderId, Id) FROM SalesHistory;
                CREATE TRIGGER OrderIdentityBeforeInsert BEFORE INSERT ON Orders
                    WHEN EXISTS (SELECT 1 FROM OrderIdentities WHERE OrderId = NEW.Id)
                    BEGIN SELECT RAISE(ABORT, 'Order identity was already used'); END;
                CREATE TRIGGER OrderIdentityAfterInsert AFTER INSERT ON Orders
                    BEGIN INSERT INTO OrderIdentities VALUES (NEW.Id); END;
                CREATE TRIGGER OrderIdentityImmutable BEFORE UPDATE OF Id ON Orders
                    BEGIN SELECT RAISE(ABORT, 'Order identity is immutable'); END;
                CREATE TRIGGER OrderIdentityLedgerNoDelete BEFORE DELETE ON OrderIdentities
                    BEGIN SELECT RAISE(ABORT, 'Order identities must remain reserved'); END;
                CREATE TRIGGER OrderIdentityLedgerNoUpdate BEFORE UPDATE ON OrderIdentities
                    BEGIN SELECT RAISE(ABORT, 'Order identities are immutable'); END;
                CREATE TRIGGER OrderNumberReservationValid BEFORE INSERT ON OrderNumberReservations
                    WHEN NEW.Number <> upper(trim(NEW.Number)) OR length(NEW.Number) NOT BETWEEN 1 AND 64
                        OR NEW.Number GLOB '*[^A-Z0-9-]*' OR substr(NEW.Number, 1, 1) NOT GLOB '[A-Z0-9]'
                    BEGIN SELECT RAISE(ABORT, 'Invalid normalized order number'); END;
                CREATE TRIGGER OrderNumberReservationNoDelete BEFORE DELETE ON OrderNumberReservations
                    BEGIN SELECT RAISE(ABORT, 'Order numbers must remain reserved'); END;
                CREATE TRIGGER OrderNumberReservationNoUpdate BEFORE UPDATE ON OrderNumberReservations
                    BEGIN SELECT RAISE(ABORT, 'Order number reservations are immutable'); END;
                """);
            foreach (var table in new[] { "Orders", "SalesHistory" })
            {
                var owner = table == "Orders" ? "NEW.Id" : "coalesce(NEW.OriginalOrderId, NEW.Id)";
                foreach (var operation in new[] { "INSERT", "UPDATE" })
                    migrationBuilder.Sql($"""
                        CREATE TRIGGER {table}Number{operation} BEFORE {operation} ON {table}
                        WHEN NOT EXISTS (SELECT 1 FROM OrderNumberReservations
                            WHERE Number = upper(trim(NEW.OrderNumber)) AND OrderId = {owner})
                        BEGIN SELECT RAISE(ABORT, 'Order number is not reserved for this identity'); END;
                        """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverting loses the permanent reservations. Back up and assess before downgrading.
            foreach (var trigger in new[] { "OrderIdentityBeforeInsert", "OrderIdentityAfterInsert", "OrderIdentityImmutable",
                "OrderIdentityLedgerNoDelete", "OrderIdentityLedgerNoUpdate", "OrderNumberReservationValid",
                "OrderNumberReservationNoDelete", "OrderNumberReservationNoUpdate", "OrdersNumberINSERT", "OrdersNumberUPDATE",
                "SalesHistoryNumberINSERT", "SalesHistoryNumberUPDATE" })
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {trigger};");
            migrationBuilder.Sql("DROP TABLE OrderIdentities;");
            migrationBuilder.DropTable(
                name: "CheckoutAttempts");

            migrationBuilder.DropTable(
                name: "OrderNumberAudits");

            migrationBuilder.DropTable(
                name: "OrderNumberReservations");

            migrationBuilder.DropColumn(
                name: "NumberRevision",
                table: "Orders");
        }
    }
}
