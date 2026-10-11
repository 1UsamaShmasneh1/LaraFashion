using LaraFashion.Data;
using LaraFashion.Models.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Runtime.InteropServices;
using System.Text.Json;

static class DeploymentSafetyTests
{
    static void Check(bool value,string message="Deployment assertion failed") { if(!value) throw new Exception(message); }
    static async Task Reject(Func<Task> action)
    { try { await action(); } catch(InvalidOperationException) { return; } throw new Exception("Expected deployment refusal"); }
    internal static async Task<Fixture> Old(int count)
    {
        var f=await Fixture.Create(previous:true);
        if(count>0) await f.SeedLegacy("DEPLOY-"+Guid.NewGuid().ToString("N").ToUpperInvariant(),false);
        await using var db=f.Db();
        if(count>1)
        {
            var row=(await DeploymentDatabase.Items(f.Paths.DatabasePath)).Single();
            await using var transaction=await db.Database.BeginTransactionAsync();
            for(var i=1;i<count;i++) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id,OrderId,ProductId,ProductName,ProductSerialNumber,ProductImageUrl,Size,Quantity,UnitPrice) VALUES ({Guid.NewGuid()},{row.OrderId},{row.ProductId},{row.ProductName},{row.ProductSerialNumber},{row.ProductImageUrl},{row.Size},{row.Quantity},{row.UnitPrice})");
            await transaction.CommitAsync();
        }
        await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.SafeNumbersMigration); return f;
    }
    internal static async Task<DeploymentPlan> MakePlan(Fixture f,string evidence="-",string sha="-")
    {
        var snapshot=Path.Combine(Path.GetDirectoryName(f.Paths.DatabasePath)!,"backup-"+Guid.NewGuid().ToString("N")+".db");
        await DeploymentDatabase.Backup(f.Paths.DatabasePath,snapshot); var plan=await DeploymentDatabase.Plan(snapshot,evidence,sha);
        plan.FrozenTargetIdentity=DatabaseFileIdentity.Get(f.Paths.DatabasePath); plan.FrozenTargetStamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath); return plan;
    }
    static async Task Full(Fixture f,DeploymentPlan plan)
    { await DeploymentDatabase.Rehearse(plan); await DeploymentDatabase.Prepare(f.Paths.DatabasePath,plan); await DeploymentDatabase.Apply(f.Paths.DatabasePath,plan); }
    static string File(Fixture f,string prefix) => Path.Combine(Path.GetDirectoryName(f.Paths.DatabasePath)!,prefix+Guid.NewGuid().ToString("N")+".json");
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateHardLink(string newName,string existing,IntPtr reserved);
    public static void Register(List<(string Name,Func<Task> Run)> tests)
    {
        void Test(string name,Func<Task> run)=>tests.Add(("deployment "+name,run));
        Test("01 complete safe-numbers evidence identity review preserves every original table",async()=>
        { var f=await Old(3); var p=await MakePlan(f); await Full(f,p); Check(p.RequiredEvidence.Count==3 && await DeploymentDatabase.Classify(f.Paths.DatabasePath)=="ReviewApplied"); });
        Test("02 dynamic zero few and over-2730 required evidence sets",async()=>
        { foreach(var count in new[]{0,2,2733}) { var f=await Old(count); var p=await MakePlan(f); await Full(f,p); Check((await DeploymentDatabase.Evidence(f.Paths.DatabasePath,"HistoricalOrderItemEvidence")).Count==count); } });
        Test("03 pre-stop arrival included and later external write refuses target preparation",async()=>
        {
            var f=await Old(1); await using(var db=f.Db()) await db.OrderItems.ExecuteUpdateAsync(i=>i.SetProperty(x=>x.Quantity,3));
            var p=await MakePlan(f); Check(p.RequiredEvidence.Single().ItemFingerprint==LaraFashion.Services.HistoricalItemFingerprint.Create((await DeploymentDatabase.Items(f.Paths.DatabasePath)).Single()));
            await DeploymentDatabase.Rehearse(p); await using(var db=f.Db()) await db.OrderItems.ExecuteUpdateAsync(i=>i.SetProperty(x=>x.Quantity,4));
            await Reject(()=>DeploymentDatabase.Prepare(f.Paths.DatabasePath,p));
        });
        Test("04 malformed arguments and zero-exit incomplete machine reports never run fixtures or overwrite DB",async()=>
        {
            var f=await Old(0); var hash=await DeploymentDatabase.Hash(f.Paths.DatabasePath);
            Check(await DeploymentMaintenance.Run(["--deployment-backup",f.Paths.DatabasePath])!=0 && hash==await DeploymentDatabase.Hash(f.Paths.DatabasePath));
            var result=File(f,"broken-"); System.IO.File.WriteAllText(result,"{\"ContractVersion\":1,\"Success\":true}");
            Check(await DeploymentMaintenance.Run(["--deployment-check-result",result,"--deployment-apply",DeploymentDatabase.CandidateCommit])!=0);
            System.IO.File.WriteAllText(result,"{"); Check(await DeploymentMaintenance.Run(["--deployment-check-result",result,"--deployment-apply",DeploymentDatabase.CandidateCommit])!=0);
        });
        Test("05 missing hash wrong schema and same physical file aliases leave source untouched",async()=>
        {
            var f=await Old(1); var p=await MakePlan(f); var source=p.SnapshotPath; var hash=await DeploymentDatabase.Hash(source);
            await Reject(()=>LegacyReviewEvidence.Prepare(f.Paths.DatabasePath,source,"WRONG"));
            await Reject(()=>LegacyReviewEvidence.Prepare(source,source,hash));
            var alias=Path.Combine(Path.GetDirectoryName(source)!,"hardlink.db");
            if(OperatingSystem.IsWindows()) Check(CreateHardLink(alias,source,IntPtr.Zero));
            else { var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList={source,alias} })!; await process.WaitForExitAsync(); Check(process.ExitCode==0); }
            Check(DatabaseFileIdentity.Get(alias)==DatabaseFileIdentity.Get(source)); await Reject(()=>LegacyReviewEvidence.Prepare(alias,source,hash));
            Check(hash==await DeploymentDatabase.Hash(source));
            var identity=await Old(0); await using(var db=identity.Db()) await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration);
            var identityHash=await DeploymentDatabase.Hash(identity.Paths.DatabasePath);
            await Reject(()=>LegacyReviewEvidence.Prepare(f.Paths.DatabasePath,identity.Paths.DatabasePath,identityHash));
        });
        Test("06 missing extra wrong fingerprint same-count replacement and source binding reject before sealing",async()=>
        {
            foreach(var mode in new[]{"missing","extra","fingerprint","replacement","source"})
            {
                var f=await Old(2); var p=await MakePlan(f); await DeploymentDatabase.Rehearse(p); await DeploymentDatabase.Prepare(f.Paths.DatabasePath,p);
                await using var db=f.Db();
                if(mode=="missing") await db.Database.ExecuteSqlRawAsync("DELETE FROM LegacyOrderItemEvidenceStaging WHERE rowid=(SELECT min(rowid) FROM LegacyOrderItemEvidenceStaging)");
                if(mode=="extra") await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO LegacyOrderItemEvidenceStaging SELECT {Guid.NewGuid()},OrderId,ProductId,ItemFingerprint,SourceSha256,SourceMigration,RegisteredAtUtc FROM LegacyOrderItemEvidenceStaging LIMIT 1");
                if(mode=="replacement") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE LegacyOrderItemEvidenceStaging SET OrderItemId={Guid.NewGuid()} WHERE rowid=(SELECT min(rowid) FROM LegacyOrderItemEvidenceStaging)");
                if(mode=="fingerprint") await db.Database.ExecuteSqlRawAsync("UPDATE LegacyOrderItemEvidenceStaging SET ItemFingerprint='INVALID'");
                if(mode=="source") await db.Database.ExecuteSqlRawAsync("UPDATE LegacyOrderItemEvidenceStaging SET SourceSha256='INVALID'");
                // Bypass only the physical-stamp test to exercise exact-set protection independently.
                p.PreparedTargetStamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
                await Reject(()=>DeploymentDatabase.Apply(f.Paths.DatabasePath,p));
                Check(!(await db.Database.GetAppliedMigrationsAsync()).Contains(DeploymentReadiness.ReviewMigration));
            }
        });
        Test("07 identity-only needs real pre-identity source and excludes modern null and changed snapshot",async()=>
        {
            var f=await Old(2); var old=await MakePlan(f); await using(var db=f.Db())
            {
                await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration);
                var item=(await DeploymentDatabase.Items(f.Paths.DatabasePath)).First();
                await db.OrderItems.Where(i=>i.Id==item.Id).ExecuteUpdateAsync(i=>i.SetProperty(x=>x.Quantity,9));
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id,OrderId,ProductId,ProductName,ProductSerialNumber,ProductImageUrl,Size,Quantity,UnitPrice,ProductSizeId) VALUES ({Guid.NewGuid()},{item.OrderId},{item.ProductId},{item.ProductName},{item.ProductSerialNumber},{item.ProductImageUrl},{item.Size},{1},{10m},{(Guid?)null})");
            }
            await Reject(()=>MakePlan(f)); var p=await MakePlan(f,old.SnapshotPath,old.SnapshotSha256);
            Check(p.RequiredEvidence.Count==1 && p.ChangedSourceLines==1 && p.UnknownNotInSource==1); await Full(f,p);
        });
        Test("08 review-applied redeploy preserves retained audit/evidence after order cleanup without reimport",async()=>
        {
            var s=await HistoricalInventoryReviewTests.Historical(); await using(var db=s.F.Db())
            {
                var service=s.F.Orders(db); var need=await service.GetInventoryReviewAsync(s.OrderId,OrderStatus.Cancelled,s.F.Token);
                var preview=await service.PreviewInventoryReviewAsync(s.OrderId,OrderStatus.Cancelled,need.Items.ToDictionary(i=>i.OrderItemId,i=>s.P.Sizes[0].Id),s.F.Token);
                await service.ConfirmInventoryReviewAsync(new(preview.OperationId,s.OrderId,preview.ProtectedTicket,"synthetic audit retention"),s.F.Token);
                await service.DeleteOrderAsync(s.OrderId);
            }
            var p=await MakePlan(s.F); Check(p.SchemaState=="ReviewApplied" && p.RequiredEvidence.Count==1); await Full(s.F,p);
            await using var fresh=s.F.Db(); Check(await fresh.OrderInventoryReviews.CountAsync()==1 && await fresh.OrderInventoryReviewLines.CountAsync()==1 && await fresh.OrderItems.CountAsync()==0);
        });
        Test("09 partial newer conflicting and closed-without-evidence databases refuse without repair",async()=>
        {
            var f=await Old(1); await using(var db=f.Db()) await db.Database.MigrateAsync(); await Reject(()=>MakePlan(f));
            var partial=await Old(0); await using(var db=partial.Db()) await db.Database.ExecuteSqlRawAsync("ALTER TABLE OrderItems ADD COLUMN ProductSizeId TEXT"); await Reject(()=>MakePlan(partial));
            var newer=await Old(0); await using(var db=newer.Db()) await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Future','9.0.16')"); await Reject(()=>MakePlan(newer));
            var collision=await Old(0); var product=await collision.Product(3); await using(var db=collision.Db()) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProductSizes (Id,ProductId,SizeName,Quantity) VALUES ({Guid.NewGuid()},{product.Id},{"S"},{3})"); await Reject(()=>MakePlan(collision));
        });
        Test("10 failure after staging and identity restores only unchanged business pair; after review verifies",async()=>
        {
            foreach(var point in new[]{"Staged","IdentityApplied","ReviewApplied"})
            {
                var f=await Old(1); var p=await MakePlan(f); await DeploymentDatabase.Rehearse(p); await DeploymentDatabase.Prepare(f.Paths.DatabasePath,p);
                if(point!="Staged") await Reject(()=>DeploymentDatabase.Apply(f.Paths.DatabasePath,p,checkpoint:stage=>stage==point ? Task.FromException(new DeploymentFailure("Injected")) : Task.CompletedTask));
                await DeploymentDatabase.Restore(f.Paths.DatabasePath,p); Check(await DeploymentDatabase.Classify(f.Paths.DatabasePath)=="BeforeIdentity");
            }
        });
        Test("11 later order or stock/other write forbids backup restore even with zero review operations",async()=>
        {
            var f=await Old(1); var p=await MakePlan(f); await Full(f,p);
            await using(var db=f.Db()) await db.OrderItems.ExecuteUpdateAsync(i=>i.SetProperty(x=>x.Quantity,7));
            var before=await DeploymentDatabase.Hash(f.Paths.DatabasePath); await Reject(()=>DeploymentDatabase.Restore(f.Paths.DatabasePath,p)); Check(before==await DeploymentDatabase.Hash(f.Paths.DatabasePath));
        });
        Test("12 backup path failure restore failure and machine failure cannot announce completion",async()=>
        {
            var f=await Old(0); var hash=await DeploymentDatabase.Hash(f.Paths.DatabasePath);
            await Reject(()=>DeploymentDatabase.Backup(f.Paths.DatabasePath,f.Paths.DatabasePath)); Check(hash==await DeploymentDatabase.Hash(f.Paths.DatabasePath));
            var p=await MakePlan(f); System.IO.File.WriteAllBytes(p.SnapshotPath,[1,2,3]); await Reject(()=>DeploymentDatabase.Restore(f.Paths.DatabasePath,p)); Check(hash==await DeploymentDatabase.Hash(f.Paths.DatabasePath));
        });
        Test("13 interrupted persisted start intent blocks automatic retry and stage skipping",async()=>
        {
            var f=await Old(0); var state=File(f,"state-"); DeploymentMaintenance.RecordState(state,"Prepared",DeploymentDatabase.CandidateCommit,"previous");
            Check(await DeploymentMaintenance.Run(["--deployment-previous-attempt",state,File(f,"result-")])!=0);
            try { DeploymentMaintenance.RecordState(state,"Completed",DeploymentDatabase.CandidateCommit,"previous"); throw new Exception("Expected stage rejection"); } catch(DeploymentFailure) { }
            DeploymentMaintenance.RecordState(state,"ManualRecoveryRequired",DeploymentDatabase.CandidateCommit,"previous"); Check(DeploymentDatabase.Load<DeploymentAttempt>(state).Stage=="ManualRecoveryRequired");
        });
        Test("14 exact guard definition required even with correct trigger name and count",async()=>
        {
            var f=await Old(0); var p=await MakePlan(f); await Full(f,p); await using(var db=f.Db())
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER HistoricalOrderItemEvidenceNoINSERT; CREATE TRIGGER HistoricalOrderItemEvidenceNoINSERT BEFORE INSERT ON HistoricalOrderItemEvidence WHEN 0 BEGIN SELECT RAISE(ABORT,'disabled'); END;");
            await Reject(()=>MakePlan(f));
        });
        Test("15 protected source stays unchanged and logical comparison covers unlisted extra business tables",async()=>
        {
            var f=await Old(1); await using(var db=f.Db()) await db.Database.ExecuteSqlRawAsync("CREATE TABLE ExtraBusiness (Id TEXT PRIMARY KEY, Value TEXT); INSERT INTO ExtraBusiness VALUES ('one','preserve');");
            var p=await MakePlan(f); var hash=await DeploymentDatabase.Hash(p.SnapshotPath); await Full(f,p); Check(hash==await DeploymentDatabase.Hash(p.SnapshotPath));
            await using(var db=f.Db()) await db.Database.ExecuteSqlRawAsync("UPDATE ExtraBusiness SET Value='changed'"); await Reject(()=>DeploymentDatabase.Verify(f.Paths.DatabasePath,p));
        });
        Test("16 health must match release schema and physical DB; GET response has no business writes",async()=>
        {
            var f=await Old(0); var p=await MakePlan(f); await Full(f,p); var baseline=await DeploymentDatabase.Capture(f.Paths.DatabasePath);
            var health=await DeploymentReadiness.CheckAsync(f.Paths.DatabasePath,DeploymentDatabase.CandidateCommit); await DeploymentDatabase.Compare(f.Paths.DatabasePath,baseline);
            var response=File(f,"health-"); DeploymentDatabase.Save(response,health with { Commit=new string('0',40) });
            Check(await DeploymentMaintenance.Run(["--deployment-health",f.Paths.DatabasePath,response,DeploymentDatabase.CandidateCommit,Environment.ProcessId.ToString(),File(f,"result-")])!=0);
            var other=await Fixture.Create(); DeploymentDatabase.Save(response,await DeploymentReadiness.CheckAsync(other.Paths.DatabasePath,DeploymentDatabase.CandidateCommit));
            Check(await DeploymentMaintenance.Run(["--deployment-health",f.Paths.DatabasePath,response,DeploymentDatabase.CandidateCommit,Environment.ProcessId.ToString(),File(f,"result-")])!=0);
            DeploymentDatabase.Save(response,health); Check(await DeploymentMaintenance.Run(["--deployment-health",f.Paths.DatabasePath,response,DeploymentDatabase.CandidateCommit,Environment.ProcessId.ToString(),File(f,"result-")])==0);
            Check(await DeploymentMaintenance.Run(["--deployment-health",f.Paths.DatabasePath,response,DeploymentDatabase.CandidateCommit,(Environment.ProcessId+1).ToString(),File(f,"result-")])!=0);
        });
        Test("17 missing explicit result contract is refused despite successful remaining fields",async()=>
        {
            var f=await Old(0); var result=File(f,"partial-contract-");
            System.IO.File.WriteAllText(result,JsonSerializer.Serialize(new { CandidateCommit=DeploymentDatabase.CandidateCommit,Command="--maintenance-contract",Success=true,FailureCode="",Gates=new Dictionary<string,bool> { ["ToolContractVerified"]=true } }));
            Check(await DeploymentMaintenance.Run(["--deployment-check-result",result,"--maintenance-contract",DeploymentDatabase.CandidateCommit])!=0);
        });
        Test("18 external runtime settings and release manifest mismatch fail without leaking secrets",async()=>
        {
            var f=await Old(0); var p=await MakePlan(f); await Full(f,p);
            var root=Path.GetDirectoryName(f.Paths.DatabasePath)!; var keys=Path.Combine(root,"keys"); Directory.CreateDirectory(keys);
            var settings=Path.Combine(root,"runtime.env"); const string secret="synthetic-private-key-not-for-logs";
            System.IO.File.WriteAllLines(settings,["ASPNETCORE_ENVIRONMENT=Production","LARAFASHION_DATABASE_PATH="+f.Paths.DatabasePath,"LARAFASHION_UPLOADS_PATH="+f.Paths.UploadsPath,"LARAFASHION_DATA_PROTECTION_PATH="+keys,"JwtSettings__SecretKey="+secret,"JwtSettings__Issuer=synthetic","JwtSettings__Audience=synthetic","JwtSettings__ExpiryMinutes=60"]);
            Check(await DeploymentMaintenance.Run(["--deployment-config",settings,f.Paths.DatabasePath,f.Paths.UploadsPath,keys,File(f,"result-")])==0);
            System.IO.File.AppendAllText(settings,"\nJwtSettings__ExpiryMinutes=0"); var failure=File(f,"result-");
            Check(await DeploymentMaintenance.Run(["--deployment-config",settings,f.Paths.DatabasePath,f.Paths.UploadsPath,keys,failure])!=0);
            Check(!System.IO.File.ReadAllText(failure).Contains(secret));
            var release=Path.Combine(root,"release"); Directory.CreateDirectory(release);
            System.IO.File.Copy(typeof(AppDbContext).Assembly.Location,Path.Combine(release,"LaraFashion.dll"));
            Check(await DeploymentMaintenance.Run(["--deployment-manifest",release,File(f,"result-")])==0);
            Check(await DeploymentMaintenance.Run(["--deployment-compatible",Path.Combine(release,"release-version.json"),f.Paths.DatabasePath,File(f,"result-")])==0);
            System.IO.File.AppendAllText(Path.Combine(release,"LaraFashion.dll"),"changed");
            Check(await DeploymentMaintenance.Run(["--deployment-compatible",Path.Combine(release,"release-version.json"),f.Paths.DatabasePath,File(f,"result-")])!=0);
        });
        Test("19 persisted start attempt forbids CLI restore even if no business write is visible",async()=>
        {
            var f=await Old(1); var p=await MakePlan(f); await Full(f,p);
            var root=Path.GetDirectoryName(f.Paths.DatabasePath)!; var plan=Path.Combine(root,"plan.json"); DeploymentDatabase.Save(plan,p);
            var state=Path.Combine(root,"state.json");
            foreach(var stage in new[]{"Prepared","WritersStopAttempted","WritersStopped","BackupVerified","PlanVerified","TargetFrozen","RehearsalVerified","EvidencePrepareAttempted","EvidenceVerified","SchemaChangeAttempted","SchemaVerified","FilesSwapAttempted","FilesSwapped","ApplicationStartAttempted"})
                DeploymentMaintenance.RecordState(state,stage,DeploymentDatabase.CandidateCommit,"previous");
            var stamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])!=0);
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath) && await DeploymentDatabase.Classify(f.Paths.DatabasePath)=="ReviewApplied");
        });
        Test("20 SQLite engine backup includes committed WAL without altering source journal or losing rows",async()=>
        {
            var f=await Old(0); await using var connection=new SqliteConnection($"Data Source={f.Paths.DatabasePath};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync(); using var command=connection.CreateCommand();
            command.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE WALBusiness (Id INTEGER PRIMARY KEY,Value TEXT); INSERT INTO WALBusiness VALUES (1,'synthetic committed WAL');"; await command.ExecuteNonQueryAsync();
            Check(new FileInfo(f.Paths.DatabasePath+"-wal").Length>0); var stamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            var backup=Path.Combine(Path.GetDirectoryName(f.Paths.DatabasePath)!,"wal-backup.db"); await DeploymentDatabase.Backup(f.Paths.DatabasePath,backup);
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath)); DeploymentDatabase.Standalone(backup);
            await using var read=await DeploymentDatabase.Read(backup); Check((await DeploymentDatabase.Rows(read,"SELECT Value FROM WALBusiness")).Single()[0]?.ToString()=="synthetic committed WAL");
            command.CommandText="PRAGMA journal_mode"; Check((await command.ExecuteScalarAsync())?.ToString()=="wal");
        });
        Test("21 CLI recovery retains current migrated database and refuses replay after success",async()=>
        {
            var f=await Old(1); var p=await MakePlan(f); await Full(f,p);
            var root=Path.GetDirectoryName(f.Paths.DatabasePath)!; var plan=Path.Combine(root,"plan.json"); DeploymentDatabase.Save(plan,p);
            var statePath=Path.Combine(root,"state.json");
            DeploymentDatabase.Save(statePath,new DeploymentAttempt { Commit=DeploymentDatabase.CandidateCommit,Stage="SchemaVerified",WritersStopped=true,
                TargetIdentity=p.FrozenTargetIdentity,SnapshotPath=p.SnapshotPath,SnapshotSha256=p.SnapshotSha256 });
            var unknown=DeploymentDatabase.Load<DeploymentAttempt>(statePath); unknown.Stage="Unknown"; DeploymentDatabase.Save(statePath,unknown);
            var unchanged=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])!=0);
            Check(unchanged==await DeploymentDatabase.Stamp(f.Paths.DatabasePath) && !DeploymentDatabase.Load<DeploymentAttempt>(statePath).DatabaseRestoreAttempted);
            unknown.Stage="SchemaVerified"; DeploymentDatabase.Save(statePath,unknown);
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])==0);
            var state=DeploymentDatabase.Load<DeploymentAttempt>(statePath);
            Check(state.DatabaseRestoreAttempted && state.BeforeRestoreSha256==await DeploymentDatabase.Hash(state.BeforeRestorePath));
            Check(await DeploymentDatabase.Classify(state.BeforeRestorePath)=="ReviewApplied" && await DeploymentDatabase.Classify(f.Paths.DatabasePath)=="BeforeIdentity");
            await DeploymentDatabase.Compare(state.BeforeRestorePath,p.Baseline,true);
            var stamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])!=0);
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath));
        });
        Test("22 CLI recovery failure retains later writes and persisted intent prevents retry",async()=>
        {
            var f=await Old(1); var p=await MakePlan(f); await Full(f,p);
            await using(var db=f.Db()) await db.OrderItems.ExecuteUpdateAsync(i=>i.SetProperty(x=>x.Quantity,7));
            var root=Path.GetDirectoryName(f.Paths.DatabasePath)!; var plan=Path.Combine(root,"plan.json"); DeploymentDatabase.Save(plan,p);
            var statePath=Path.Combine(root,"state.json");
            DeploymentDatabase.Save(statePath,new DeploymentAttempt { Commit=DeploymentDatabase.CandidateCommit,Stage="SchemaVerified",WritersStopped=true,
                TargetIdentity=p.FrozenTargetIdentity,SnapshotPath=p.SnapshotPath,SnapshotSha256=p.SnapshotSha256 });
            var stamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])!=0);
            var state=DeploymentDatabase.Load<DeploymentAttempt>(statePath);
            Check(state.DatabaseRestoreAttempted && state.BeforeRestoreSha256==await DeploymentDatabase.Hash(state.BeforeRestorePath));
            await DeploymentDatabase.Compare(state.BeforeRestorePath,await DeploymentDatabase.Capture(f.Paths.DatabasePath));
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath));
            Check(await DeploymentMaintenance.Run(["--deployment-restore",f.Paths.DatabasePath,plan,File(f,"result-")])!=0);
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath));
        });
        Test("23 recovery health must prove previous release schema database and PID without changing data",async()=>
        {
            var f=await Old(1); var root=Path.GetDirectoryName(f.Paths.DatabasePath)!;
            var release=Path.Combine(root,"previous"); Directory.CreateDirectory(release);
            var dll=Path.Combine(release,"LaraFashion.dll"); System.IO.File.WriteAllText(dll,"synthetic previous binary");
            var manifest=Path.Combine(release,"release-version.json"); var commit=new string('1',40);
            DeploymentDatabase.Save(manifest,new ReleaseManifest(1,commit,DeploymentReadiness.SafeNumbersMigration,await DeploymentDatabase.Hash(dll)));
            var health=Path.Combine(root,"recovery-health.json");
            var expected=new DeploymentReadiness.Health(1,"healthy",commit,DeploymentReadiness.SafeNumbersMigration,DatabaseFileIdentity.Get(f.Paths.DatabasePath),123);
            var stamp=await DeploymentDatabase.Stamp(f.Paths.DatabasePath);
            DeploymentDatabase.Save(health,expected);
            Check(await DeploymentMaintenance.Run(["--deployment-recovery-health",f.Paths.DatabasePath,health,manifest,"123",File(f,"result-")])==0);
            foreach(var invalid in new[]{expected with { Commit=DeploymentDatabase.CandidateCommit },expected with { Migration=DeploymentReadiness.ReviewMigration },expected with { DatabaseIdentity="wrong" },expected with { ProcessId=124 }})
            {
                DeploymentDatabase.Save(health,invalid);
                Check(await DeploymentMaintenance.Run(["--deployment-recovery-health",f.Paths.DatabasePath,health,manifest,"123",File(f,"result-")])!=0);
            }
            System.IO.File.WriteAllText(health,"{\"status\":\"healthy\"}");
            Check(await DeploymentMaintenance.Run(["--deployment-recovery-health",f.Paths.DatabasePath,health,manifest,"123",File(f,"result-")])!=0);
            Check(stamp==await DeploymentDatabase.Stamp(f.Paths.DatabasePath));
        });
    }
}
