using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed class DeploymentFailure(string code) : InvalidOperationException(code) { public string Code { get; } = code; }
sealed record TableSnapshot(string[] Columns, string Digest);
sealed record DatabaseSnapshot(Dictionary<string, TableSnapshot> Tables, Dictionary<string, string> Triggers, string[] Migrations);
sealed record EvidenceRow(Guid OrderItemId, Guid OrderId, Guid ProductId, string ItemFingerprint, string SourceSha256, string SourceMigration);
sealed class DeploymentPlan
{
    public int ContractVersion { get; set; } = 1;
    public string CandidateCommit { get; set; } = DeploymentDatabase.CandidateCommit;
    public string SchemaState { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string EvidenceSourcePath { get; set; } = "";
    public string EvidenceSourceSha256 { get; set; } = "";
    public DatabaseSnapshot Baseline { get; set; } = null!;
    public List<EvidenceRow> RequiredEvidence { get; set; } = [];
    public int SourceLines { get; set; }
    public int ChangedSourceLines { get; set; }
    public int MissingSourceLines { get; set; }
    public int AlreadyIdentifiedSourceLines { get; set; }
    public int UnknownNotInSource { get; set; }
    public string FrozenTargetIdentity { get; set; } = "";
    public string FrozenTargetStamp { get; set; } = "";
    public string PreparedTargetStamp { get; set; } = "";
    public bool RehearsalVerified { get; set; }
}

static class DeploymentDatabase
{
    public static string CandidateCommit => typeof(AppDbContext).Assembly.GetCustomAttributes(false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+').Last();
    public static void Require(bool condition, string code) { if (!condition) throw new DeploymentFailure(code); }
    public static AppDbContext Context(string path) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options);
    public static async Task<string> Hash(string path)
    { await using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); return Convert.ToHexString(await SHA256.HashDataAsync(file)); }
    public static async Task<string> Stamp(string path) => await Hash(path) + ":" + (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length>0 ? await Hash(path + "-wal") : "none");
    public static void Existing(string path) => Require(File.Exists(path) && new FileInfo(path).Length > 0, "DatabaseMissingOrEmpty");
    public static void Standalone(string path)
    {
        Existing(path);
        Require(!File.Exists(path + "-wal") || new FileInfo(path + "-wal").Length == 0, "EvidenceRequiresStandaloneBackup");
    }
    public static async Task<SqliteConnection> Read(string path)
    {
        Existing(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(); return db;
    }
    public static async Task<List<object?[]>> Rows(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql; using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>(); while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        return rows;
    }
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    public static string Digest<T>(IEnumerable<T> values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(values.Select(v => JsonSerializer.Serialize(v)).Order(StringComparer.Ordinal)))));
    public static async Task Integrity(string path)
    {
        await using var db = await Read(path);
        Require((await Rows(db, "PRAGMA integrity_check")).All(r => (string?)r[0] == "ok"), "IntegrityFailed");
        Require((await Rows(db, "PRAGMA foreign_key_check")).Count == 0, "ForeignKeysFailed");
    }
    public static async Task Backup(string source, string destination)
    {
        Existing(source); Require(!File.Exists(destination), "BackupAlreadyExists");
        var stamp = await Stamp(source);
        await using (var input = await Read(source))
        await using (var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await output.OpenAsync(); input.BackupDatabase(output);
            using var cmd = output.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=DELETE"; await cmd.ExecuteScalarAsync();
        }
        Standalone(destination); await Integrity(destination);
        Require(stamp == await Stamp(source), "WriterChangedDatabaseDuringBackup");
        await Compare(source, await Capture(destination));
    }
    public static async Task<DatabaseSnapshot> Capture(string path, DatabaseSnapshot? shape = null)
    {
        await using var db = await Read(path);
        var tables = new Dictionary<string, TableSnapshot>();
        var names = shape is null ? (await Rows(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' AND name <> 'LegacyOrderItemEvidenceStaging' ORDER BY name")).Select(r => (string)r[0]!).ToArray() : shape.Tables.Keys.ToArray();
        foreach (var name in names)
        {
            var columns = shape is null ? (await Rows(db, $"PRAGMA table_info({Quote(name)})")).Select(r => (string)r[1]!).ToArray() : shape.Tables[name].Columns;
            var rows = await Rows(db, $"SELECT {string.Join(',', columns.Select(Quote))} FROM {Quote(name)}");
            tables[name] = new(columns, Digest(rows));
        }
        var triggers = (await Rows(db, "SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name")).ToDictionary(r => (string)r[0]!, r => DeploymentReadiness.NormalizeSql((string)r[1]!));
        var migrations = (await Rows(db, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId")).Select(r => (string)r[0]!).ToArray();
        return new(tables, triggers, migrations);
    }
    public static async Task Compare(string path, DatabaseSnapshot baseline, bool allowMigrationChanges = false)
    {
        var current = await Capture(path, baseline);
        Require(baseline.Tables.All(t => current.Tables[t.Key].Digest == t.Value.Digest), "BusinessDataChanged");
        Require(baseline.Triggers.All(t => current.Triggers.TryGetValue(t.Key, out var sql) && sql == t.Value), "PreviousGuardChanged");
        if (!allowMigrationChanges) Require(current.Migrations.SequenceEqual(baseline.Migrations), "MigrationHistoryChanged");
        await using var db = await Read(path);
        var names = (await Rows(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")).Select(r => (string)r[0]!);
        var technical = new[] { "LegacyOrderItemEvidenceStaging", "ProductSizeIdentities", "HistoricalOrderItemEvidence", "OrderInventoryReviews", "OrderInventoryReviewLines", "OrderInventoryReviewMovements" };
        Require(names.All(n => baseline.Tables.ContainsKey(n) || (allowMigrationChanges && technical.Contains(n)) || n == "LegacyOrderItemEvidenceStaging"), "UnexpectedTableCreated");
    }
    public static async Task<string> Classify(string path)
    {
        await Integrity(path); var actual = await Capture(path);
        await using var context = Context(path); var candidate = context.Database.GetMigrations().ToArray();
        Require(actual.Migrations.Length > 0 && actual.Migrations.SequenceEqual(candidate.Take(actual.Migrations.Length)), "UnknownOrPartialMigrationHistory");
        var latest = actual.Migrations.Last();
        Require(new[] { DeploymentReadiness.SafeNumbersMigration, DeploymentReadiness.IdentityMigration, DeploymentReadiness.ReviewMigration }.Contains(latest), "UnsupportedSchemaState");
        var root = Path.Combine(Path.GetTempPath(), "LaraFashionSchema", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var canonical = Path.Combine(root, "schema.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={canonical};Pooling=False").Options;
            await using (var expectedContext = new AppDbContext(options)) await expectedContext.GetService<IMigrator>().MigrateAsync(latest);
            await using var expected = await Read(canonical); await using var target = await Read(path);
            var tableNames = (await Rows(expected, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")).Select(r => (string)r[0]!);
            foreach (var name in tableNames)
            {
                // Names/types/nullability/defaults/PKs and FK definitions, not merely migration labels.
                Require(Digest(await Rows(expected, $"PRAGMA table_info({Quote(name)})")) == Digest(await Rows(target, $"PRAGMA table_info({Quote(name)})")), "PartialTableSchema");
                Require(Digest(await Rows(expected, $"PRAGMA foreign_key_list({Quote(name)})")) == Digest(await Rows(target, $"PRAGMA foreign_key_list({Quote(name)})")), "PartialForeignKeySchema");
            }
            var expectedIndexes = await Rows(expected, "SELECT name,sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL");
            var targetIndexes = (await Rows(target, "SELECT name,sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL")).ToDictionary(r => (string)r[0]!, r => DeploymentReadiness.NormalizeSql((string)r[1]!));
            Require(expectedIndexes.All(r => targetIndexes.TryGetValue((string)r[0]!, out var sql) && sql == DeploymentReadiness.NormalizeSql((string)r[1]!)), "PartialIndexSchema");
            Require(DeploymentReadiness.ExpectedTriggers(context, latest).All(t => actual.Triggers.TryGetValue(t.Key, out var sql) && sql == t.Value), "GuardsIncompatible");
            var sizes = await Rows(target, "SELECT Id,ProductId,SizeName,Quantity,typeof(Quantity) FROM ProductSizes");
            var products = (await Rows(target, "SELECT Id FROM Products")).Select(r => (string)r[0]!).ToHashSet(StringComparer.Ordinal);
            Require(sizes.All(s => Guid.TryParse((string)s[0]!, out var id) && id != Guid.Empty && Guid.TryParse((string)s[1]!, out var product) && product != Guid.Empty
                && products.Contains((string)s[1]!) && SizeNamePolicy.IsValid((string)s[2]!) && (string)s[4]! == "integer" && Convert.ToInt64(s[3]) is >= 0 and <= int.MaxValue)
                && !sizes.GroupBy(s => ((string)s[1]!, SizeNamePolicy.Key((string)s[2]!))).Any(g => g.Count() > 1), "SizePreflightFailed");
            if (latest != DeploymentReadiness.SafeNumbersMigration)
                Require((await Rows(target, "SELECT SizeName,NormalizedName FROM ProductSizes")).All(s => SizeNamePolicy.Key((string)s[0]!) == (string)s[1]!), "SizeKeyMismatch");
            if (latest == DeploymentReadiness.SafeNumbersMigration)
                Require((await Rows(target, "SELECT count(*) FROM pragma_table_info('OrderItems') WHERE name='ProductSizeId'")).All(r => Convert.ToInt32(r[0]) == 0)
                    && (await Rows(target, "SELECT name FROM sqlite_master WHERE name IN ('HistoricalOrderItemEvidence','OrderInventoryReviews','OrderInventoryReviewLines','OrderInventoryReviewMovements','ProductSizeIdentities')")).Count == 0, "PartialFutureSchema");
            if (latest == DeploymentReadiness.IdentityMigration)
                Require((await Rows(target, "SELECT name FROM sqlite_master WHERE name IN ('HistoricalOrderItemEvidence','OrderInventoryReviews','OrderInventoryReviewLines','OrderInventoryReviewMovements')")).Count == 0, "PartialFutureSchema");
        }
        finally { Directory.Delete(root, true); }
        return latest == DeploymentReadiness.SafeNumbersMigration ? "BeforeIdentity" : latest == DeploymentReadiness.IdentityMigration ? "IdentityOnly" : "ReviewApplied";
    }
    public static async Task<List<OrderItem>> Items(string path)
    {
        await using var db = await Read(path);
        var identity = (await Rows(db, "SELECT name FROM pragma_table_info('OrderItems') WHERE name='ProductSizeId'")).Count == 1;
        return (await Rows(db, "SELECT i.Id,i.OrderId,i.ProductId,i.ProductName,i.ProductSerialNumber,i.ProductImageUrl,i.Size,i.Quantity,i.UnitPrice," + (identity ? "i.ProductSizeId" : "NULL") + " FROM OrderItems i JOIN Orders o ON o.Id=i.OrderId")).Select(r => new OrderItem {
            Id=Guid.Parse((string)r[0]!), OrderId=Guid.Parse((string)r[1]!), ProductId=Guid.Parse((string)r[2]!), ProductName=(string)r[3]!, ProductSerialNumber=(string)r[4]!, ProductImageUrl=(string)r[5]!, Size=(string)r[6]!, Quantity=Convert.ToInt32(r[7]), UnitPrice=Convert.ToDecimal(r[8],System.Globalization.CultureInfo.InvariantCulture), ProductSizeId=r[9] is null ? null : Guid.Parse((string)r[9]!) }).ToList();
    }
    public static async Task<List<EvidenceRow>> Evidence(string path, string table)
    {
        Require(table is "HistoricalOrderItemEvidence" or "LegacyOrderItemEvidenceStaging", "InvalidEvidenceTable");
        await using var db = await Read(path);
        Require((await Rows(db, $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'")).Count == 1, "EvidenceTableMissing");
        return (await Rows(db, $"SELECT OrderItemId,OrderId,ProductId,ItemFingerprint,SourceSha256,SourceMigration FROM {table}")).Select(r => new EvidenceRow(Guid.Parse((string)r[0]!),Guid.Parse((string)r[1]!),Guid.Parse((string)r[2]!), (string)r[3]!, (string)r[4]!, (string)r[5]!)).ToList();
    }
    public static async Task<DeploymentPlan> Plan(string snapshot, string evidence, string expectedHash)
    {
        Standalone(snapshot); var state = await Classify(snapshot);
        var plan = new DeploymentPlan { SchemaState=state, SnapshotPath=Path.GetFullPath(snapshot), SnapshotSha256=await Hash(snapshot), Baseline=await Capture(snapshot) };
        var current = await Items(snapshot);
        if (state == "ReviewApplied")
        {
            plan.RequiredEvidence = await Evidence(snapshot, "HistoricalOrderItemEvidence");
            var unknown = current.Where(i => i.ProductSizeId is null).ToList();
            Require(unknown.All(i => plan.RequiredEvidence.Any(e => e.OrderItemId == i.Id && e.OrderId == i.OrderId && e.ProductId == i.ProductId && e.ItemFingerprint == HistoricalItemFingerprint.Create(i))), "ClosedEligibilityNeedsSeparateRepair");
            await using var closed = await Read(snapshot);
            Require((await Rows(closed,"SELECT name FROM sqlite_master WHERE name='LegacyOrderItemEvidenceStaging'")).Count==0, "UnexpectedClosedStaging");
            return plan;
        }
        if (state == "BeforeIdentity") { evidence = snapshot; expectedHash = plan.SnapshotSha256; }
        Require(evidence != "-" && !string.IsNullOrWhiteSpace(evidence), "PreIdentityEvidenceRequired");
        Standalone(evidence); Require(string.Equals(await Hash(evidence), expectedHash, StringComparison.OrdinalIgnoreCase), "EvidenceHashMismatch");
        Require(await Classify(evidence) == "BeforeIdentity", "EvidenceMustPrecedeIdentity");
        if (state == "IdentityOnly") DatabaseFileIdentity.RequireDistinct(evidence, snapshot);
        plan.EvidenceSourcePath=Path.GetFullPath(evidence); plan.EvidenceSourceSha256=await Hash(evidence);
        var originals = await Items(evidence); plan.SourceLines=originals.Count; var map = current.ToDictionary(i=>i.Id);
        foreach (var original in originals)
        {
            if (!map.TryGetValue(original.Id,out var item)) { plan.MissingSourceLines++; continue; }
            if (HistoricalItemFingerprint.Create(item)!=HistoricalItemFingerprint.Create(original)) { plan.ChangedSourceLines++; continue; }
            if (item.ProductSizeId.HasValue) { plan.AlreadyIdentifiedSourceLines++; continue; }
            plan.RequiredEvidence.Add(new(item.Id,item.OrderId,item.ProductId,HistoricalItemFingerprint.Create(item),plan.EvidenceSourceSha256,plan.Baseline.Migrations.Last()));
        }
        // SourceMigration describes the evidence, not the intermediate target schema.
        var sourceMigrations=(await Capture(evidence)).Migrations.Last();
        plan.RequiredEvidence=plan.RequiredEvidence.Select(e=>e with { SourceMigration=sourceMigrations }).ToList();
        plan.UnknownNotInSource=current.Count(i=>i.ProductSizeId is null && originals.All(o=>o.Id!=i.Id));
        Require(plan.EvidenceSourceSha256==await Hash(evidence), "EvidenceChanged"); return plan;
    }
    public static async Task SourceUnchanged(DeploymentPlan plan)
    {
        Require(plan.ContractVersion==1 && plan.CandidateCommit==CandidateCommit, "PlanVersionMismatch");
        Standalone(plan.SnapshotPath); Require(await Hash(plan.SnapshotPath)==plan.SnapshotSha256, "SnapshotChanged");
        if (plan.EvidenceSourcePath.Length>0) { Standalone(plan.EvidenceSourcePath); Require(await Hash(plan.EvidenceSourcePath)==plan.EvidenceSourceSha256, "EvidenceChanged"); }
    }
    public static async Task ExactEvidence(string path, DeploymentPlan plan, string table)
    { Require(Digest(await Evidence(path,table))==Digest(plan.RequiredEvidence), "EvidenceSetMismatch"); }
    public static async Task Prepare(string target, DeploymentPlan plan)
    {
        await SourceUnchanged(plan); Existing(target); await Compare(target,plan.Baseline);
        Require(DatabaseFileIdentity.Get(target)==plan.FrozenTargetIdentity && await Stamp(target)==plan.FrozenTargetStamp, "TargetChangedOutsideDeployment");
        if (plan.SchemaState=="ReviewApplied") { await ExactEvidence(target,plan,"HistoricalOrderItemEvidence"); return; }
        DatabaseFileIdentity.RequireDistinct(plan.EvidenceSourcePath,target);
        await using (var db=await Read(target))
            if ((await Rows(db,"SELECT name FROM sqlite_master WHERE name='LegacyOrderItemEvidenceStaging'")).Count>0) await ExactEvidence(target,plan,"LegacyOrderItemEvidenceStaging");
        await LegacyReviewEvidence.Prepare(target,plan.EvidenceSourcePath,plan.EvidenceSourceSha256);
        await ExactEvidence(target,plan,"LegacyOrderItemEvidenceStaging"); plan.PreparedTargetStamp=await Stamp(target);
    }
    public static async Task Apply(string target, DeploymentPlan plan, bool rehearsal=false, Func<string,Task>? checkpoint=null)
    {
        await SourceUnchanged(plan); await Compare(target,plan.Baseline);
        if (!rehearsal)
            Require(plan.RehearsalVerified && DatabaseFileIdentity.Get(target)==plan.FrozenTargetIdentity
                && await Stamp(target)==(plan.PreparedTargetStamp.Length>0 ? plan.PreparedTargetStamp : plan.FrozenTargetStamp), "TargetChangedOutsideDeployment");
        Require(await Classify(target)==plan.SchemaState,"TargetSchemaChanged");
        if (plan.SchemaState!="ReviewApplied")
        {
            await ExactEvidence(target,plan,"LegacyOrderItemEvidenceStaging");
            await using var db=Context(target);
            if (plan.SchemaState=="BeforeIdentity") await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration);
            if(checkpoint is not null) await checkpoint("IdentityApplied");
            await ExactEvidence(target,plan,"LegacyOrderItemEvidenceStaging"); await Compare(target,plan.Baseline,true);
            await db.Database.MigrateAsync();
            if(checkpoint is not null) await checkpoint("ReviewApplied");
        }
        await Verify(target,plan);
    }
    public static async Task Verify(string target,DeploymentPlan plan)
    {
        await SourceUnchanged(plan); Require(await Classify(target)=="ReviewApplied","SchemaVerificationFailed");
        await Compare(target,plan.Baseline,true); await ExactEvidence(target,plan,"HistoricalOrderItemEvidence");
        await using var db=await Read(target);
        Require((await Rows(db,"SELECT name FROM sqlite_master WHERE name='LegacyOrderItemEvidenceStaging'")).Count==0,"StagingNotClosed");
        if(plan.SchemaState!="ReviewApplied")
        {
            var items=await Items(target);
            Require(plan.RequiredEvidence.All(e=>items.Any(i=>i.Id==e.OrderItemId && i.ProductSizeId is null && HistoricalItemFingerprint.Create(i)==e.ItemFingerprint)),"HistoricalIdentityChanged");
            Require((await Rows(db,"SELECT count(*) FROM OrderInventoryReviews")).All(r=>Convert.ToInt64(r[0])==0),"MigrationCreatedReview");
            Require((await Rows(db,"SELECT count(*) FROM OrderInventoryReviewLines")).All(r=>Convert.ToInt64(r[0])==0)
                && (await Rows(db,"SELECT count(*) FROM OrderInventoryReviewMovements")).All(r=>Convert.ToInt64(r[0])==0),"MigrationCreatedMovement");
        }
    }
    public static async Task Rehearse(DeploymentPlan plan)
    {
        await SourceUnchanged(plan); var working=Path.Combine(Path.GetDirectoryName(plan.SnapshotPath)!,"rehearsal-"+Guid.NewGuid().ToString("N")+".db");
        await Backup(plan.SnapshotPath,working);
        if(plan.SchemaState!="ReviewApplied") { await LegacyReviewEvidence.Prepare(working,plan.EvidenceSourcePath,plan.EvidenceSourceSha256); await ExactEvidence(working,plan,"LegacyOrderItemEvidenceStaging"); }
        await Apply(working,plan,true); plan.RehearsalVerified=true;
    }
    public static async Task Restore(string target,DeploymentPlan plan)
    {
        await SourceUnchanged(plan); Require(DatabaseFileIdentity.Get(target)==plan.FrozenTargetIdentity,"TargetIdentityChanged");
        // A backup is not authorization to discard later business writes, even while stopped.
        await Compare(target,plan.Baseline,true);
        if(plan.SchemaState!="ReviewApplied")
        {
            await using var current=await Read(target);
            foreach(var table in new[]{"OrderInventoryReviews","OrderInventoryReviewLines","OrderInventoryReviewMovements"})
                if((await Rows(current,$"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'")).Count>0)
                    Require((await Rows(current,$"SELECT count(*) FROM {table}")).All(r=>Convert.ToInt64(r[0])==0),"LaterReviewWriteForbidsRestore");
        }
        await using(var source=await Read(plan.SnapshotPath))
        await using(var output=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=target,Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString()))
        { await output.OpenAsync(); source.BackupDatabase(output); }
        await Integrity(target); await Compare(target,plan.Baseline);
        Require(await Classify(target)==plan.SchemaState,"RestoreSchemaMismatch");
    }
    public static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new DeploymentFailure("MissingMachineResult");
    public static void Save<T>(string path,T value)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { JsonSerializer.Serialize(file,value); file.Flush(true); }
        File.Move(temporary,path,true);
        if(OperatingSystem.IsLinux()) DurableDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
    [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int open(string path,int flags);
    [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int fsync(int descriptor);
    [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int close(int descriptor);
    static void DurableDirectory(string path)
    {
        var descriptor=open(path,0x10000); // Linux O_DIRECTORY | O_RDONLY.
        if(descriptor<0) throw new IOException("StateDirectorySyncFailed");
        try { if(fsync(descriptor)!=0) throw new IOException("StateDirectorySyncFailed"); }
        finally { close(descriptor); }
    }
}
