using LaraFashion.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LaraFashion.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260711120000_AddStoreReports")]
public partial class AddStoreReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IsSandbox", table: "Orders", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.CreateTable(name: "SalesHistory", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            OriginalOrderId = table.Column<Guid>(type: "TEXT", nullable: true),
            OrderNumber = table.Column<string>(type: "TEXT", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            CustomerName = table.Column<string>(type: "TEXT", nullable: false),
            PhoneNumber = table.Column<string>(type: "TEXT", nullable: false),
            TotalQuantity = table.Column<int>(type: "INTEGER", nullable: false),
            FinalTotal = table.Column<decimal>(type: "TEXT", nullable: false),
            LastStatus = table.Column<int>(type: "INTEGER", nullable: false),
            StatusUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SalesHistory", x => x.Id));
        migrationBuilder.CreateTable(name: "StoreVisits", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            VisitorIdHash = table.Column<string>(type: "TEXT", nullable: false),
            StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            LastActivityAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_StoreVisits", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_SalesHistory_CreatedAtUtc", table: "SalesHistory", column: "CreatedAtUtc");
        migrationBuilder.CreateIndex(name: "IX_SalesHistory_LastStatus", table: "SalesHistory", column: "LastStatus");
        migrationBuilder.CreateIndex(name: "IX_SalesHistory_OriginalOrderId", table: "SalesHistory", column: "OriginalOrderId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_SalesHistory_PhoneNumber", table: "SalesHistory", column: "PhoneNumber");
        migrationBuilder.CreateIndex(name: "IX_StoreVisits_StartedAtUtc", table: "StoreVisits", column: "StartedAtUtc");
        migrationBuilder.CreateIndex(name: "IX_StoreVisits_VisitorIdHash_LastActivityAtUtc", table: "StoreVisits", columns: new[] { "VisitorIdHash", "LastActivityAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SalesHistory");
        migrationBuilder.DropTable(name: "StoreVisits");
        migrationBuilder.DropColumn(name: "IsSandbox", table: "Orders");
    }
}
