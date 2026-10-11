using Microsoft.Data.Sqlite;
using System.Text.Json;
using LaraFashion.Services;

static class Diagnostics
{
    public static async Task<int> Run(string path)
    {
        // Explicit path only; no AppStoragePaths/production/env auto-discovery, no migrations.
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync();
        await SizePreflight(db);
        await ReviewDiagnostics.Run(db);
        var queries = new Dictionary<string, string>
        {
            ["engine_and_migrations"] = "SELECT sqlite_version() AS SQLiteVersion, (SELECT max(MigrationId) FROM __EFMigrationsHistory) AS LastMigration",
            ["internal_identity_schema"] = "SELECT name, type, pk FROM pragma_table_info('Orders') WHERE name = 'Id'",
            ["positive_stock_excluded_not_necessarily_corrupt"] = "SELECT p.Id AS ProductId, p.IsActive, p.IsPublished, CASE WHEN p.IsActive = 0 THEN 'inactive' WHEN p.IsPublished = 0 THEN 'intentionally-unpublished-or-needs-incident-evidence' ELSE 'no-valid-positive-size' END AS Reason FROM Products p WHERE EXISTS (SELECT 1 FROM ProductSizes s WHERE s.ProductId = p.Id AND s.Quantity > 0) AND (p.IsActive = 0 OR p.IsPublished = 0 OR NOT EXISTS (SELECT 1 FROM ProductSizes s WHERE s.ProductId = p.Id AND s.Quantity > 0 AND trim(s.SizeName) <> ''))",
            ["invalid_or_missing_sizes"] = "SELECT p.Id AS ProductId, s.Id AS SizeId, s.Quantity, CASE WHEN s.Id IS NULL THEN 'no-size-rows-source-required' ELSE 'invalid-quantity-or-label' END AS Reason FROM Products p LEFT JOIN ProductSizes s ON s.ProductId = p.Id WHERE s.Id IS NULL OR s.Quantity < 0 OR trim(s.SizeName) = ''",
            ["duplicate_size_labels"] = "SELECT ProductId, SizeName, count(*) AS Rows FROM ProductSizes GROUP BY ProductId, SizeName HAVING count(*) > 1",
            ["display_number_collisions_distinct_logical_owners"] = "WITH numbers AS (SELECT upper(trim(OrderNumber)) AS Number, Id AS Owner FROM Orders UNION ALL SELECT upper(trim(OrderNumber)), coalesce(OriginalOrderId, Id) FROM SalesHistory) SELECT Number, count(DISTINCT Owner) AS LogicalOwners, group_concat(DISTINCT Owner) AS InternalIdentities FROM numbers GROUP BY Number HAVING count(DISTINCT Owner) > 1",
            ["invalid_numbers"] = "SELECT Id, OrderNumber, 'order' AS Source FROM Orders WHERE length(trim(OrderNumber)) NOT BETWEEN 1 AND 64 OR upper(trim(OrderNumber)) GLOB '*[^A-Z0-9-]*' OR substr(upper(trim(OrderNumber)),1,1) NOT GLOB '[A-Z0-9]' UNION ALL SELECT Id, OrderNumber, 'history' FROM SalesHistory WHERE length(trim(OrderNumber)) NOT BETWEEN 1 AND 64 OR upper(trim(OrderNumber)) GLOB '*[^A-Z0-9-]*' OR substr(upper(trim(OrderNumber)),1,1) NOT GLOB '[A-Z0-9]'",
            ["same_order_and_snapshot_display_only"] = "SELECT o.Id AS OrderId, h.Id AS HistoryId, o.OrderNumber FROM Orders o JOIN SalesHistory h ON h.OriginalOrderId = o.Id WHERE o.OrderNumber = h.OrderNumber",
            ["identity_reuse_or_link_mismatch_suspicions_not_proof"] = "SELECT o.Id AS OrderId, h.Id AS HistoryId, o.OrderNumber AS LiveNumber, h.OrderNumber AS HistoricalNumber FROM Orders o JOIN SalesHistory h ON h.OriginalOrderId = o.Id WHERE upper(trim(o.OrderNumber)) <> upper(trim(h.OrderNumber)) OR o.IsSandbox = 1 OR o.FinalTotal <> h.FinalTotal",
            ["unlinked_history_never_infer_owner_by_number"] = "SELECT Id AS HistoryId, OrderNumber FROM SalesHistory WHERE OriginalOrderId IS NULL",
            ["foreign_key_check"] = "PRAGMA foreign_key_check"
        };
        foreach (var (section, sql) in queries)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
            Console.WriteLine(JsonSerializer.Serialize(new { Section = section, Rows = rows }));
        }
        return 0;
    }

    private sealed record SizeRow(string Id, string ProductId, string RawName, object Quantity, string QuantityType)
    {
        public string Key => SizeNamePolicy.Key(RawName);
        public bool Valid => SizeNamePolicy.IsValid(RawName) && QuantityType == "integer"
            && Convert.ToInt64(Quantity) is >= 0 and <= int.MaxValue
            && Guid.TryParse(Id, out var id) && id != Guid.Empty && id.ToString("D").ToUpperInvariant() == Id
            && Guid.TryParse(ProductId, out var product) && product != Guid.Empty && product.ToString("D").ToUpperInvariant() == ProductId;
    }
    private static async Task SizePreflight(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, SizeName, Quantity, typeof(Quantity) FROM ProductSizes ORDER BY ProductId, Id";
        var sizes = new List<SizeRow>();
        using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) sizes.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetValue(3), reader.GetString(4)));
        var collisions = sizes.GroupBy(s => (s.ProductId, s.Key)).Where(g => g.Count() > 1).ToList();
        cmd.CommandText = "SELECT Id FROM Products";
        var productIds = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) productIds.Add(reader.GetString(0));
        var invalid = sizes.Where(s => !s.Valid || !productIds.Contains(s.ProductId)).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "size_identity_preflight", CanUpgradeSizes = collisions.Count == 0 && invalid.Count == 0,
            CollisionGroups = collisions.Count, InvalidRows = invalid.Count, AffectedProducts = collisions.Select(g => g.Key.ProductId).Concat(invalid.Select(s => s.ProductId)).Distinct().Count(),
            Action = "Read only. Stop upgrade on conflicts; never merge rows, infer history identities, delete or change quantities." }));
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "normalized_size_collisions", Rows = collisions.Select(g => new { g.Key.ProductId, NormalizedKey = g.Key.Key,
            Rows = g.Select(s => new { SizeId = s.Id, s.RawName, s.Quantity, s.QuantityType, CodePoints = string.Join(" ", s.RawName.EnumerateRunes().Select(r => $"U+{r.Value:X4}")) }) }) }));
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "invalid_size_names_quantities_identities", Rows = invalid }));
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('OrderItems') WHERE name = 'ProductSizeId'";
        var hasIdentity = Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
        cmd.CommandText = hasIdentity
            ? "SELECT i.Id, i.OrderId, i.ProductId, i.ProductSizeId, i.Size FROM OrderItems i LEFT JOIN ProductSizes s ON s.Id = i.ProductSizeId AND s.ProductId = i.ProductId WHERE i.ProductSizeId IS NULL OR s.Id IS NULL"
            : "SELECT Id, OrderId, ProductId, NULL, Size FROM OrderItems";
        var unmappable = new List<object>();
        using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) unmappable.Add(new { ItemId = reader.GetString(0), OrderId = reader.GetString(1), ProductId = reader.GetString(2),
                ProductSizeId = reader.IsDBNull(3) ? null : reader.GetString(3), SizeSnapshot = reader.GetString(4), Reason = "historical-or-missing-identity-needs-evidence-no-label-backfill" });
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "order_items_without_provable_size_identity", Rows = unmappable }));
        if (hasIdentity)
        {
            cmd.CommandText = "SELECT i.Id, i.OrderId, i.ProductId, i.ProductSizeId, i.Size, s.SizeName FROM OrderItems i JOIN ProductSizes s ON s.Id = i.ProductSizeId AND s.ProductId = i.ProductId";
            var changed = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) if (SizeNamePolicy.Key(reader.GetString(4)) != SizeNamePolicy.Key(reader.GetString(5)))
                changed.Add(new { ItemId = reader.GetString(0), OrderId = reader.GetString(1), ProductId = reader.GetString(2), SizeId = reader.GetString(3), Reason = "selection-snapshot-changed" });
            Console.WriteLine(JsonSerializer.Serialize(new { Section = "order_size_snapshot_changes", Rows = changed }));
        }
    }
}
