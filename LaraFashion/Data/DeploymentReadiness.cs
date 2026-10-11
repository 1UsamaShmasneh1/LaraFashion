using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LaraFashion.Data;

// Read-only runtime gate. It never migrates, seeds, repairs, or creates a database.
public static class DeploymentReadiness
{
    public const int ContractVersion = 1;
    public const string IdentityMigration = "20261005162501_AddStableProductSizeIdentity";
    public const string ReviewMigration = "20261005191438_AddHistoricalOrderInventoryReview";
    public const string SafeNumbersMigration = "20261003154113_AddSafeOrderNumbers";
    public sealed record Health(int ContractVersion, string Status, string Commit, string Migration, string DatabaseIdentity, int ProcessId);

    public static string NormalizeSql(string sql) => Regex.Replace(sql.Replace("\"", ""), @"\s+", " ").Trim().TrimEnd(';');
    public static Dictionary<string, string> ExpectedTriggers(AppDbContext db, string? through = null)
    {
        var sql = db.GetService<IMigrator>().GenerateScript("0", through ?? ReviewMigration);
        return Regex.Matches(sql, @"CREATE TRIGGER\s+(\w+)\b[\s\S]*?END\s*;", RegexOptions.IgnoreCase)
            .ToDictionary(m => m.Groups[1].Value, m => NormalizeSql(m.Value), StringComparer.Ordinal);
    }
    public static async Task<Health> CheckAsync(string path, string commit)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("DatabaseMissing");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        if (!applied.SequenceEqual(db.Database.GetMigrations())) throw new InvalidOperationException("SchemaIncompatible");
        await db.Database.OpenConnectionAsync();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger'";
        var actual = new Dictionary<string, string>();
        using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) actual[reader.GetString(0)] = NormalizeSql(reader.GetString(1));
        if (ExpectedTriggers(db).Any(t => !actual.TryGetValue(t.Key, out var sql) || sql != t.Value)) throw new InvalidOperationException("GuardsIncompatible");
        // These reads fail on a partial schema even if its migration history was forged.
        command.CommandText = "SELECT InventoryRevision FROM Orders LIMIT 0; SELECT ReviewRevision,NormalizedName FROM ProductSizes LIMIT 0; SELECT ProductSizeId FROM OrderItems LIMIT 0; SELECT * FROM HistoricalOrderItemEvidence LIMIT 0; SELECT * FROM OrderInventoryReviews LIMIT 0; SELECT * FROM OrderInventoryReviewLines LIMIT 0; SELECT * FROM OrderInventoryReviewMovements LIMIT 0;";
        await command.ExecuteNonQueryAsync();
        return new(ContractVersion, "healthy", commit, ReviewMigration, DatabaseFileIdentity.Get(path), Environment.ProcessId);
    }
}

public static class DatabaseFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsInfo
    { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInfo info);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(int fd, IntPtr buffer);
    public static string Get(string path)
    {
        using var handle = File.OpenHandle(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string identity;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("FileIdentityUnavailable");
            identity = $"windows:{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
        }
        else if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                if (fstat(handle.DangerousGetHandle().ToInt32(), buffer) != 0) throw new IOException("FileIdentityUnavailable");
                identity = $"linux:{Marshal.ReadInt64(buffer, 0)}:{Marshal.ReadInt64(buffer, 8)}";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else throw new PlatformNotSupportedException("FileIdentityUnavailable");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public static void RequireDistinct(string source, string target)
    {
        if (Get(source) == Get(target)) throw new InvalidOperationException("SourceTargetAlias");
    }
}
