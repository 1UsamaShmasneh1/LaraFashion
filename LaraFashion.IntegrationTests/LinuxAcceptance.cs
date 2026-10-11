using System.Diagnostics;
using System.Text.Json;
using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using LaraFashion.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// Private test runner only. No new application endpoint or production service registration.
static class LinuxAcceptance
{
    internal const string Marker = "LaraFashion synthetic Linux acceptance v1";
    internal static string Root(string input)
    {
        var root = Path.GetFullPath(input);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        DeploymentDatabase.Require(OperatingSystem.IsLinux() && root.StartsWith(temporary, StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(root, ".larafashion-linux-acceptance")) == Marker, "SyntheticLinuxRootRequired");
        return root;
    }
    static string Child(string root, string input)
    {
        var path = Path.GetFullPath(Path.Combine(root, input));
        DeploymentDatabase.Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "SyntheticPathRequired");
        return path;
    }
    public static async Task<int> Run(string[] args)
    {
        try
        {
            DeploymentDatabase.Require(args.Length >= 3, "InvalidLinuxTestArguments");
            var root = Root(args[1]); var target = Path.Combine(root, "test.db");
            switch (args[2])
            {
                case "existing-key-probe":
                    DeploymentDatabase.Require(args.Length == 3, "InvalidLinuxTestArguments");
                    var home = Path.Combine(root,"home"); Directory.CreateDirectory(home);
                    Environment.SetEnvironmentVariable("HOME",home);
                    var keyDirectory = Path.Combine(home,".aspnet","DataProtection-Keys"); Directory.CreateDirectory(keyDirectory);
                    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { ContentRootPath=root,EnvironmentName="Production" });
                    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
                    using(var syntheticHost=builder.Build()) syntheticHost.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("SyntheticOnly").Protect("synthetic initialization");
                    ProductionKeyProbe.Verify(keyDirectory,root);
                    break;
                case "init":
                    DeploymentDatabase.Require(args.Length == 3 && !File.Exists(target), "FixtureAlreadyExists");
                    var scenario = await HistoricalInventoryReviewTests.Historical();
                    await using (var db = scenario.F.Db())
                    {
                        await db.AdminUsers.ExecuteDeleteAsync();
                        db.AdminUsers.Add(new AdminUser { Id = Guid.NewGuid(), Username = "linux-acceptance-admin",
                            PasswordHash = new PasswordHasherService().HashPassword("linux-synthetic-test-only") });
                        await db.SaveChangesAsync();
                    }
                    await DeploymentDatabase.Backup(scenario.F.Paths.DatabasePath, target);
                    DeploymentDatabase.Save(Path.Combine(root, "fixture.json"), new { scenario.OrderId, SizeId = scenario.P.Sizes[0].Id, Stock = 3, ExpectedStock = 5 });
                    break;
                case "identity":
                    DeploymentDatabase.Require(args.Length >= 4, "InvalidLinuxTestArguments");
                    Console.WriteLine(JsonSerializer.Serialize(args.Skip(3).Select(p => DatabaseFileIdentity.Get(Child(root, p))).ToArray()));
                    break;
                case "snapshot":
                    DeploymentDatabase.Require(args.Length == 4, "InvalidLinuxTestArguments");
                    DeploymentDatabase.Save(Child(root, args[3]), await DeploymentDatabase.Capture(target));
                    break;
                case "compare":
                    DeploymentDatabase.Require(args.Length == 4, "InvalidLinuxTestArguments");
                    await DeploymentDatabase.Compare(target, DeploymentDatabase.Load<DatabaseSnapshot>(Child(root, args[3])));
                    break;
                case "state-save":
                    DeploymentDatabase.Require(args.Length == 4, "InvalidLinuxTestArguments");
                    var statePath = Child(root, args[3]);
                    for (var i = 0; i < 1000; i++)
                    {
                        DeploymentDatabase.Save(statePath, new DeploymentAttempt { Commit = DeploymentDatabase.CandidateCommit,
                            Stage = i.ToString(), Stages = Enumerable.Repeat(new string('x', 4096), 8).ToList() });
                        Console.WriteLine("SAVED " + i); Console.Out.Flush();
                        if (Console.ReadLine() != "NEXT") break;
                    }
                    break;
                case "state-read":
                    DeploymentDatabase.Require(args.Length == 4, "InvalidLinuxTestArguments");
                    var state = DeploymentDatabase.Load<DeploymentAttempt>(Child(root, args[3]));
                    DeploymentDatabase.Require(state.ContractVersion == 1 && state.Commit == DeploymentDatabase.CandidateCommit
                        && state.Stages.Count == 8 && state.Stages.All(s => s == new string('x', 4096)), "InvalidPersistedState");
                    Console.WriteLine("READ " + state.Stage);
                    break;
                case "recovery-start-state":
                    DeploymentDatabase.Require(args.Length == 3, "InvalidLinuxTestArguments");
                    var old = await DeploymentSafetyTests.Old(1);
                    var plan = await DeploymentSafetyTests.MakePlan(old);
                    await DeploymentDatabase.Rehearse(plan);
                    await DeploymentDatabase.Prepare(old.Paths.DatabasePath, plan);
                    await DeploymentDatabase.Apply(old.Paths.DatabasePath, plan);
                    var planPath = Path.Combine(root, "plan.json"); DeploymentDatabase.Save(planPath, plan);
                    var attemptPath = Path.Combine(root, "state.json");
                    foreach (var step in new[] { "Prepared", "WritersStopAttempted", "WritersStopped", "BackupVerified", "PlanVerified",
                        "TargetFrozen", "RehearsalVerified", "EvidencePrepareAttempted", "EvidenceVerified", "SchemaChangeAttempted",
                        "SchemaVerified", "FilesSwapAttempted", "FilesSwapped", "RecoveryStartAttempted" })
                        DeploymentMaintenance.RecordState(attemptPath, step, DeploymentDatabase.CandidateCommit, "synthetic-previous");
                    var started = DeploymentDatabase.Load<DeploymentAttempt>(attemptPath);
                    DeploymentDatabase.Require(started.ApplicationStartAttempted && !started.DatabaseRestoreAttempted, "RecoveryStartIntentNotPersisted");
                    DeploymentDatabase.Save(Path.Combine(root, "recovery-start.json"), new { Target = old.Paths.DatabasePath, Plan = planPath,
                        Stamp = await DeploymentDatabase.Stamp(old.Paths.DatabasePath), ManagedHeapBudgetBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes });
                    break;
                default: throw new DeploymentFailure("UnknownLinuxTestCommand");
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Never serialize a JWT, protected review ticket, key, or connection string.
            Console.WriteLine(JsonSerializer.Serialize(new { Success = false, FailureCode = ex is DeploymentFailure f ? f.Code : ex.GetType().Name }));
            return 2;
        }
    }
    internal static async Task ProbeHost(IHost host, string root, string phase)
    {
        var output = Path.Combine(root, "probe-" + phase + ".json");
        try
        {
            using var scope = host.Services.CreateScope(); var services = scope.ServiceProvider;
            var environment = services.GetRequiredService<IHostEnvironment>();
            var paths = services.GetRequiredService<AppStoragePaths>();
            DeploymentDatabase.Require(environment.IsProduction() && paths.DatabasePath == Path.Combine(root, "test.db")
                && Path.GetFullPath(Environment.GetEnvironmentVariable("LARAFASHION_DATA_PROTECTION_PATH")!) == Path.Combine(root, "keys"), "HostFixtureMismatch");
            var discriminator = services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator;
            // Observe the application's default discriminator; never set or override it for this test.
            DeploymentDatabase.Require(discriminator == environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                "UnexpectedApplicationDiscriminator");
            var repository = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
            DeploymentDatabase.Require(repository is Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository fs
                && fs.Directory.FullName == Path.Combine(root, "keys"), "WrongActualKeyRepository");
            // Seeder has completed before ApplicationStarted. Compare it separately from the review and HTTP.
            await DeploymentDatabase.Compare(paths.DatabasePath,
                DeploymentDatabase.Load<DatabaseSnapshot>(Path.Combine(root, "before-" + phase + ".json")));
            var admin = services.GetRequiredService<AdminAuthService>();
            var token = await admin.LoginAsync("linux-acceptance-admin", "linux-synthetic-test-only");
            DeploymentDatabase.Require(token is not null, "SyntheticAdminLoginFailed");
            var orders = services.GetRequiredService<OrderService>();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixture.json")));
            var orderId = fixture.RootElement.GetProperty("OrderId").GetGuid();
            var sizeId = fixture.RootElement.GetProperty("SizeId").GetGuid();
            var requestPath = Path.Combine(root, "request.json"); bool replayed = false;
            if (phase == "preview")
            {
                var need = await orders.GetInventoryReviewAsync(orderId, OrderStatus.Cancelled, token!);
                var preview = await orders.PreviewInventoryReviewAsync(orderId, OrderStatus.Cancelled,
                    need.Items.ToDictionary(i => i.OrderItemId, _ => sizeId), token!);
                DeploymentDatabase.Save(requestPath, new ConfirmInventoryReview(preview.OperationId, orderId, preview.ProtectedTicket, "Synthetic Linux restart acceptance"));
            }
            else
            {
                var request = DeploymentDatabase.Load<ConfirmInventoryReview>(requestPath);
                var result = await orders.ConfirmInventoryReviewAsync(request, token!);
                replayed = result.Replayed;
                DeploymentDatabase.Require(replayed == (phase == "replay"), "UnexpectedReplayResult");
            }
            var db = services.GetRequiredService<AppDbContext>();
            var stock = await db.ProductSizes.Where(s => s.Id == sizeId).Select(s => s.Quantity).SingleAsync();
            var movements = await db.OrderInventoryReviewMovements.CountAsync();
            DeploymentDatabase.Require(stock == (phase == "preview" ? 3 : 5) && movements == (phase == "preview" ? 0 : 1), "SecondInventoryMovementOrWrongStock");
            // Force key ring resolution on the exact provider used by OrderService, including replay.
            var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("LaraFashion.OrderInventoryReview.V1");
            protector.Unprotect(DeploymentDatabase.Load<ConfirmInventoryReview>(requestPath).ProtectedTicket);
            DeploymentDatabase.Save(output, new { Success = true, Phase = phase, ProcessId = Environment.ProcessId,
                Discriminator = discriminator, KeyRepository = Path.Combine(root, "keys"), Stock = stock, Movements = movements,
                Replayed = replayed, StartupBusinessPreserved = true });
        }
        catch (Exception ex) { DeploymentDatabase.Save(output, new { Success = false, FailureCode = ex is DeploymentFailure f ? f.Code : ex.GetType().Name }); }
    }
}

// Loaded only when DOTNET_STARTUP_HOOKS explicitly names this private runner DLL.
// Diagnostic HostBuilt gives the real published application's provider, not a replacement host.
public static class StartupHook
{
    public static void Initialize()
    {
        var input = Environment.GetEnvironmentVariable("LARAFASHION_LINUX_TEST_ROOT");
        if (input is null) return;
        var root = LinuxAcceptance.Root(input);
        var phase = Environment.GetEnvironmentVariable("LARAFASHION_LINUX_TEST_PHASE");
        DeploymentDatabase.Require(phase is "preview" or "confirm" or "replay", "InvalidHostProbePhase");
        DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.Extensions.Hosting") listener.Subscribe(new Observer<KeyValuePair<string, object?>>(value =>
            {
                if (value.Key == "HostBuilt" && value.Value is IHost host)
                    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
                        _ = Task.Run(() => LinuxAcceptance.ProbeHost(host, root, phase!)));
            }));
        }));
    }
    sealed class Observer<T>(Action<T> action) : IObserver<T>
    {
        public void OnNext(T value) => action(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
