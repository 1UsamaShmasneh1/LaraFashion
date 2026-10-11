using LaraFashion.Models;
using LaraFashion.Models.Enums;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

static class ReviewDiagnostics
{
    public static async Task Run(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        async Task<bool> Exists(string table) { cmd.Parameters.Clear(); cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$table";
            cmd.Parameters.AddWithValue("$table", table); return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1; }
        async Task<bool> Column(string table, string column) { cmd.Parameters.Clear(); cmd.CommandText = "SELECT count(*) FROM pragma_table_info($table) WHERE name=$column";
            cmd.Parameters.AddWithValue("$table", table); cmd.Parameters.AddWithValue("$column", column); return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1; }
        var identity = await Column("OrderItems", "ProductSizeId");
        var evidenceTable = await Exists("HistoricalOrderItemEvidence"); var auditTable = await Exists("OrderInventoryReviews");
        var schema = identity && evidenceTable && auditTable && await Exists("OrderInventoryReviewLines")
            && await Exists("OrderInventoryReviewMovements") && await Column("Orders", "InventoryRevision") && await Column("ProductSizes", "ReviewRevision");
        cmd.Parameters.Clear(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger'";
        var triggers = new HashSet<string>(); using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) triggers.Add(reader.GetString(0));
        var required = new[] { "OrderInventoryRevisionGuard", "ProductSizeReviewRevisionGuard", "ProductSizeReviewRevisionInsertGuard", "ProductSizeReviewRevisionAdvance", "HistoricalOrderItemEvidenceNoINSERT", "HistoricalOrderItemNoReinsert" }
            .Concat(new[] { "HistoricalOrderItemEvidence", "OrderInventoryReviews", "OrderInventoryReviewLines", "OrderInventoryReviewMovements" }.SelectMany(t => new[] { t + "NoUPDATE", t + "NoDELETE" })).ToArray();
        var guards = required.All(triggers.Contains);
        cmd.CommandText = "SELECT Id FROM Products"; var products = new HashSet<Guid>();
        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) if (Guid.TryParse(reader.GetString(0), out var id)) products.Add(id);
        cmd.CommandText = "SELECT Id,ProductId,SizeName,Quantity,typeof(Quantity) FROM ProductSizes";
        var sizes = new List<ProductSize>();
        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync())
            if (Guid.TryParse(reader.GetString(0), out var id) && id != Guid.Empty && Guid.TryParse(reader.GetString(1), out var product) && product != Guid.Empty
                && reader.GetString(4) == "integer" && reader.GetInt64(3) is >= 0 and <= int.MaxValue && SizeNamePolicy.IsValid(reader.GetString(2)))
                sizes.Add(new() { Id = id, ProductId = product, SizeName = reader.GetString(2), Quantity = reader.GetInt32(3) });
        // Count every row for collision detection, including invalid/zero rows.
        cmd.CommandText = "SELECT ProductId,SizeName FROM ProductSizes"; var keyCounts = new Dictionary<(string, string), int>();
        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync())
        { var key = (reader.GetString(0), SizeNamePolicy.Key(reader.GetString(1))); keyCounts[key] = keyCounts.GetValueOrDefault(key) + 1; }
        sizes = sizes.Where(s => products.Contains(s.ProductId) && keyCounts.GetValueOrDefault((s.ProductId.ToString().ToUpperInvariant(), SizeNamePolicy.Key(s.SizeName))) == 1).ToList();
        var evidence = new Dictionary<Guid, string>();
        if (evidenceTable)
        { cmd.CommandText = "SELECT OrderItemId,ItemFingerprint FROM HistoricalOrderItemEvidence"; using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) evidence[reader.GetGuid(0)] = reader.GetString(1); }
        var priorTargets = new Dictionary<Guid, (Guid Target, string Key)>();
        var operations = 0L;
        if (schema)
        {
            cmd.CommandText = "SELECT count(*) FROM OrderInventoryReviews"; operations = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            cmd.CommandText = "SELECT l.OrderItemId,l.TargetSizeId,l.TargetSizeKey FROM OrderInventoryReviews r JOIN OrderInventoryReviewLines l ON l.OperationId=r.OperationId WHERE l.IsHistorical=1 ORDER BY r.NewRevision";
            using var reader = await cmd.ExecuteReaderAsync(); while (await reader.ReadAsync()) priorTargets[reader.GetGuid(0)] = (reader.GetGuid(1), reader.GetString(2));
        }
        cmd.CommandText = "SELECT Status,IsSandbox,count(*) FROM Orders GROUP BY Status,IsSandbox ORDER BY Status,IsSandbox";
        var states = new List<object>(); using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync())
        { var status = (OrderStatus)reader.GetInt32(0); states.Add(new { Status = status.ToString(), IsSandbox = reader.GetBoolean(1), Orders = reader.GetInt32(2), IsOpen = status != OrderStatus.Ready && status != OrderStatus.Cancelled }); }
        cmd.CommandText = "SELECT o.Status,o.IsSandbox,i.Id,i.OrderId,i.ProductId,i.ProductName,i.ProductSerialNumber,i.ProductImageUrl,i.Size,i.Quantity,i.UnitPrice," + (identity ? "i.ProductSizeId" : "NULL") + " FROM Orders o JOIN OrderItems i ON i.OrderId=o.Id";
        var groups = new Dictionary<(OrderStatus Status, bool Sandbox), Counters>();
        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync())
        {
            var key = ((OrderStatus)reader.GetInt32(0), reader.GetBoolean(1)); if (!groups.TryGetValue(key, out var c)) groups[key] = c = new();
            c.Lines++; var orderId = reader.GetGuid(3); c.Orders.Add(orderId);
            if (!reader.IsDBNull(11)) { c.TrustedIdentityPresent++; continue; }
            c.Unknown++; var item = new OrderItem { Id = reader.GetGuid(2), OrderId = orderId, ProductId = reader.GetGuid(4), ProductName = reader.GetString(5),
                ProductSerialNumber = reader.GetString(6), ProductImageUrl = reader.GetString(7), Size = reader.GetString(8), Quantity = reader.GetInt32(9), UnitPrice = reader.GetDecimal(10) };
            var eligible = evidence.TryGetValue(item.Id, out var fingerprint) && fingerprint == HistoricalItemFingerprint.Create(item);
            if (eligible) c.Eligible++; else c.Unproven++;
            var reviewed = priorTargets.TryGetValue(item.Id, out var prior);
            if (reviewed) c.PreviouslyReviewed++;
            else if (eligible) c.UnreviewedEligible++;
            if (!products.Contains(item.ProductId)) { c.MissingProduct++; c.BlockedOrders.Add(orderId); continue; }
            c.ExistingProduct++;
            var candidates = sizes.Where(s => s.ProductId == item.ProductId).ToList();
            if (reviewed)
            { candidates = candidates.Where(s => s.Id == prior.Target && SizeNamePolicy.Key(s.SizeName) == prior.Key).ToList(); }
            if (candidates.Count == 0) { c.NoValidDestination++; c.BlockedOrders.Add(orderId); }
            else { c.WithDestination++; if (candidates.Any(s => SizeNamePolicy.Key(s.SizeName) == SizeNamePolicy.Key(item.Size))) c.MatchingNameCandidateOnly++; }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "order_status_sandbox_counts", Rows = states }));
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "historical_inventory_review_counts", Rows = groups.OrderBy(x => x.Key.Status).ThenBy(x => x.Key.Sandbox).Select(x => new {
            Status = x.Key.Status.ToString(), IsSandbox = x.Key.Sandbox, Orders = x.Value.Orders.Count, x.Value.Lines, UnknownHistoricalIdentityLines = x.Value.Unknown,
            EvidenceVerifiedEligibleLines = x.Value.Eligible, NoVerifiedEvidenceLines = x.Value.Unproven, x.Value.ExistingProduct, x.Value.MissingProduct,
            x.Value.WithDestination, x.Value.NoValidDestination, OrdersWithUnavailableDestination = x.Value.BlockedOrders.Count,
            x.Value.MatchingNameCandidateOnly, x.Value.PreviouslyReviewed, UnreviewedEligibleLines = x.Value.UnreviewedEligible }) }));
        Console.WriteLine(JsonSerializer.Serialize(new { Section = "inventory_review_feature_readiness", SchemaPresent = schema, RequiredGuardsPresent = guards,
            SchemaReadyForReview = schema && guards, ExecutedAdministrativeOperations = operations,
            EvidenceVerifiedEligibleLines = groups.Values.Sum(c => c.Eligible), UnknownWithoutVerifiedEvidence = groups.Values.Sum(c => c.Unproven),
            ValidDestinationLines = groups.Values.Sum(c => c.WithDestination), MissingProductLines = groups.Values.Sum(c => c.MissingProduct),
            Conclusion = "NULL is expected for history, not proof of eligibility or a deployment failure. Schema/guards/evidence are distinct from behavioral tests and case-specific manager decisions. Name matches are candidates only; deployment needs a fresh snapshot and review." }));
    }
    sealed class Counters
    {
        public HashSet<Guid> Orders = []; public HashSet<Guid> BlockedOrders = [];
        public int Lines, TrustedIdentityPresent, Unknown, Eligible, Unproven, ExistingProduct, MissingProduct, WithDestination, NoValidDestination, MatchingNameCandidateOnly, PreviouslyReviewed, UnreviewedEligible;
    }
}
