using LaraFashion.Models;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using LaraFashion.Data;

static class LegacyReviewEvidence
{
    public static async Task<int> Prepare(string workingPath, string evidencePath, string expectedSha256)
    {
        workingPath = Path.GetFullPath(workingPath); evidencePath = Path.GetFullPath(evidencePath);
        if (string.Equals(workingPath, evidencePath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(workingPath) || !File.Exists(evidencePath)) throw new InvalidOperationException("Distinct existing working copy and evidence snapshot required.");
        DatabaseFileIdentity.RequireDistinct(evidencePath, workingPath);
        async Task<string> Hash() { await using var file = File.OpenRead(evidencePath); return Convert.ToHexString(await SHA256.HashDataAsync(file)); }
        var sourceHash = await Hash();
        if (!string.Equals(sourceHash, expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Evidence SHA256 mismatch; nothing written.");
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = evidencePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await source.OpenAsync();
        using var check = source.CreateCommand(); check.CommandText = "SELECT count(*) FROM pragma_table_info('OrderItems') WHERE name='ProductSizeId'";
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 0) throw new InvalidOperationException("Evidence must precede the size identity schema; NULL alone is not evidence.");
        check.CommandText = "PRAGMA integrity_check";
        if ((string?)await check.ExecuteScalarAsync() != "ok") throw new InvalidOperationException("Evidence integrity failed.");
        check.CommandText = "SELECT max(MigrationId) FROM __EFMigrationsHistory";
        var migration = (string)(await check.ExecuteScalarAsync())!;
        check.CommandText = "SELECT count(*) FROM __EFMigrationsHistory WHERE MigrationId='20261005162501_AddStableProductSizeIdentity'";
        if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 0) throw new InvalidOperationException("Evidence migration history already contains the identity upgrade.");
        var originals = await ReadItems(source);
        if (await Hash() != sourceHash) throw new InvalidOperationException("Evidence changed while reading; nothing written.");
        await using var work = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = workingPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await work.OpenAsync();
        using var cmd = work.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='HistoricalOrderItemEvidence'";
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0) throw new InvalidOperationException("Prepare before the review migration; post-upgrade evidence import requires a separate reviewed procedure.");
        await using var transaction = await work.BeginTransactionAsync(); cmd.Transaction = (SqliteTransaction)transaction;
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info('OrderItems') WHERE name='ProductSizeId'";
        var hasIdentity = Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0;
        var current = (await ReadItems(work, (SqliteTransaction)transaction, hasIdentity)).ToDictionary(i => i.Id);
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS LegacyOrderItemEvidenceStaging (OrderItemId TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, ProductId TEXT NOT NULL, ItemFingerprint TEXT NOT NULL, SourceSha256 TEXT NOT NULL, SourceMigration TEXT NOT NULL, RegisteredAtUtc TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
        var count = 0;
        foreach (var original in originals)
        {
            if (!current.TryGetValue(original.Id, out var item) || item.ProductSizeId.HasValue
                || HistoricalItemFingerprint.Create(item) != HistoricalItemFingerprint.Create(original)) continue;
            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT OR IGNORE INTO LegacyOrderItemEvidenceStaging VALUES ($id,$order,$product,$fingerprint,$hash,$migration,$utc)";
            cmd.Parameters.AddWithValue("$id", item.Id.ToString().ToUpperInvariant());
            cmd.Parameters.AddWithValue("$order", item.OrderId.ToString().ToUpperInvariant());
            cmd.Parameters.AddWithValue("$product", item.ProductId.ToString().ToUpperInvariant());
            cmd.Parameters.AddWithValue("$fingerprint", HistoricalItemFingerprint.Create(item));
            cmd.Parameters.AddWithValue("$hash", sourceHash); cmd.Parameters.AddWithValue("$migration", migration);
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow);
            count += await cmd.ExecuteNonQueryAsync();
        }
        if (await Hash() != sourceHash) throw new InvalidOperationException("Evidence changed; staging rolled back.");
        await transaction.CommitAsync();
        Console.WriteLine($"Legacy evidence staged: {count}; original size identities and inventory unchanged; source SHA256 {sourceHash}.");
        return count;
    }

    private static async Task<List<OrderItem>> ReadItems(SqliteConnection db, SqliteTransaction? transaction = null, bool hasIdentity = false)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "SELECT i.Id,i.OrderId,i.ProductId,i.ProductName,i.ProductSerialNumber,i.ProductImageUrl,i.Size,i.Quantity,i.UnitPrice," +
            (hasIdentity ? "i.ProductSizeId" : "NULL") + " FROM OrderItems i JOIN Orders o ON o.Id=i.OrderId ORDER BY i.Id";
        var items = new List<OrderItem>(); using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) items.Add(new() { Id = reader.GetGuid(0), OrderId = reader.GetGuid(1), ProductId = reader.GetGuid(2),
            ProductName = reader.GetString(3), ProductSerialNumber = reader.GetString(4), ProductImageUrl = reader.GetString(5),
            Size = reader.GetString(6), Quantity = reader.GetInt32(7), UnitPrice = reader.GetDecimal(8),
            ProductSizeId = reader.IsDBNull(9) ? null : reader.GetGuid(9) });
        return items;
    }
}
