using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text.Json;

// Test CLI only, in the private runner. Every call requires an explicit TEMP marker.
static class DeploymentHarness
{
    static string Root(string input)
    {
        var root=Path.GetFullPath(input); var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        DeploymentDatabase.Require(root.StartsWith(temporary,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)
            && File.Exists(Path.Combine(root,".larafashion-deploy-harness"))
            && File.ReadAllText(Path.Combine(root,".larafashion-deploy-harness"))=="LaraFashion synthetic deployment harness v1","SyntheticHarnessRequired"); return root;
    }
    public static async Task<int> Run(string[] args)
    {
        DeploymentDatabase.Require(args.Length>=3,"InvalidHarnessArguments"); var root=Root(args[1]); var target=Path.Combine(root,"persistent","data","larafashion.db");
        switch(args[2])
        {
            case "create":
                DeploymentDatabase.Require(args.Length==5 && !File.Exists(target),"InvalidHarnessArguments");
                var fixture=await DeploymentSafetyTests.Old(int.Parse(args[3])); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if(args[4]=="identity")
                { await DeploymentDatabase.Backup(fixture.Paths.DatabasePath,Path.Combine(root,"pre-identity.db")); await using var db=fixture.Db(); await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration); }
                if(args[4]=="review") { var plan=await DeploymentSafetyTests.MakePlan(fixture); await DeploymentDatabase.Rehearse(plan); await DeploymentDatabase.Prepare(fixture.Paths.DatabasePath,plan); await DeploymentDatabase.Apply(fixture.Paths.DatabasePath,plan); }
                await DeploymentDatabase.Backup(fixture.Paths.DatabasePath,target);
                var live=Path.Combine(root,"persistent","publish"); Directory.CreateDirectory(live);
                File.WriteAllText(Path.Combine(live,"LaraFashion.dll"),"synthetic previous release");
                var schema=(await DeploymentDatabase.Capture(target)).Migrations.Last();
                DeploymentDatabase.Save(Path.Combine(live,"release-version.json"),new ReleaseManifest(1,new string('0',40),schema,await DeploymentDatabase.Hash(Path.Combine(live,"LaraFashion.dll"))));
                break;
            case "write":
                await SyntheticOrder(target); break;
            case "health":
                DeploymentDatabase.Require(args.Length==5 && Path.GetFullPath(args[3]).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"InvalidHarnessArguments");
                var liveManifest=DeploymentDatabase.Load<ReleaseManifest>(Path.Combine(root,"persistent","publish","release-version.json"));
                var health=liveManifest.Commit==DeploymentDatabase.CandidateCommit
                    ? (await DeploymentReadiness.CheckAsync(target,DeploymentDatabase.CandidateCommit)) with { ProcessId=123 }
                    : new DeploymentReadiness.Health(1,"healthy",liveManifest.Commit,liveManifest.Migration,DatabaseFileIdentity.Get(target),123);
                if(args[4]=="wrong") health=health with { Commit=new string('0',40) };
                DeploymentDatabase.Save(args[3],health); break;
            case "migrate-identity":
                await using(var db=DeploymentDatabase.Context(target)) await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration); break;
            case "review-failure":
                await using(var db=DeploymentDatabase.Context(target)) await db.GetService<IMigrator>().MigrateAsync(DeploymentReadiness.IdentityMigration);
                var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={target};Mode=ReadWrite;Pooling=False").AddInterceptors(new FailReviewMigration()).Options;
                try { await using var db=new AppDbContext(options); await db.Database.MigrateAsync(); throw new Exception("Injection was not reached"); }
                catch(DeploymentFailure ex) when(ex.Code=="InjectedMigrationFailure") { }
                break;
            case "inspect":
                DeploymentDatabase.Require(args.Length==5,"InvalidHarnessArguments");
                var states=Directory.GetFiles(Path.Combine(root,"persistent","backups","deploy-attempts"),"state.json",SearchOption.AllDirectories).Select(DeploymentDatabase.Load<DeploymentAttempt>).ToArray();
                DeploymentDatabase.Require(states.Any(s=>s.Stage==args[3]),"ExpectedHarnessStageMissing");
                var items=await DeploymentDatabase.Items(target); DeploymentDatabase.Require(items.Count==int.Parse(args[4]),"HarnessBusinessRowsLost");
                await DeploymentDatabase.Integrity(target); Console.WriteLine(JsonSerializer.Serialize(new { Stage=args[3],Items=items.Count,Schema=await DeploymentDatabase.Classify(target),Success=true })); break;
            default: throw new DeploymentFailure("UnknownHarnessCommand");
        }
        return 0;
    }
    static async Task SyntheticOrder(string path)
    {
        await using var db=DeploymentDatabase.Context(path); var customer=new CustomerInfo { Id=Guid.NewGuid(),FullName="Harness",PhoneNumber="0500000000",City="Synthetic" };
        db.Customers.Add(customer); await db.SaveChangesAsync(); var id=Guid.NewGuid(); var product=Guid.NewGuid(); var number="HARNESS-"+id.ToString("N").ToUpperInvariant(); var now=DateTime.UtcNow;
        db.OrderNumberReservations.Add(new() { Number=number,OrderId=id }); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Orders (Id,OrderNumber,CustomerId,Status,CreatedAt,UpdatedAt,OriginalTotal,DiscountAmount,DiscountName,FinalTotal,IsSandbox) VALUES ({id},{number},{customer.Id},{0},{now},{now},{10m},{0m},{""},{10m},{false})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id,OrderId,ProductId,ProductName,ProductSerialNumber,ProductImageUrl,Size,Quantity,UnitPrice) VALUES ({Guid.NewGuid()},{id},{product},{"Synthetic"},{""},{""},{"S"},{1},{10m})");
    }
    sealed class FailReviewMigration : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("CREATE TABLE \"OrderInventoryReviews\"",StringComparison.Ordinal)) throw new DeploymentFailure("InjectedMigrationFailure");
            return base.NonQueryExecutingAsync(command,eventData,result,cancellationToken);
        }
    }
}
