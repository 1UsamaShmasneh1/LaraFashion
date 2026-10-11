using LaraFashion.Models;
using LaraFashion.Models.Enums;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

static class HistoricalInventoryReviewTests
{
    internal sealed record Scenario(Fixture F, Product P, Guid OrderId, string SourcePath, string SourceHash);
    static void Check(bool value, string message = "Historical review assertion failed") { if (!value) throw new Exception(message); }
    static async Task Reject(Func<Task> action)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
    static async Task Unauthorized(Func<Task> action)
    { try { await action(); } catch (UnauthorizedAccessException) { return; } throw new Exception("Expected authorization rejection"); }
    internal static async Task<Scenario> Historical(bool cancelled = false, int stock = 3, int[]? quantities = null, bool sandbox = false)
    {
        var f = await Fixture.Create(previous: true); var p = await f.Product(stock, 4); var id = Guid.NewGuid();
        quantities ??= [2];
        await using (var db = f.Db())
        {
            var customer = f.Customer(); db.Customers.Add(customer); await db.SaveChangesAsync(); var now = DateTime.Now;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Orders (Id,OrderNumber,CustomerId,Status,CreatedAt,UpdatedAt,OriginalTotal,DiscountAmount,DiscountName,FinalTotal,IsSandbox) VALUES ({id},{"HIST-" + id.ToString("N").ToUpperInvariant()},{customer.Id},{(cancelled ? 4 : 0)},{now},{now},{20m},{0m},{""},{20m},{sandbox})");
            foreach (var quantity in quantities)
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderItems (Id,OrderId,ProductId,ProductName,ProductSerialNumber,ProductImageUrl,Size,Quantity,UnitPrice) VALUES ({Guid.NewGuid()},{id},{p.Id},{p.Name},{p.SerialNumber},{p.ImageUrl},{"S"},{quantity},{10m})");
            if (!sandbox) db.SalesHistory.Add(new() { Id = Guid.NewGuid(), OriginalOrderId = id, OrderNumber = "HIST-" + id.ToString("N").ToUpperInvariant(),
                CreatedAtUtc = now.ToUniversalTime(), FinalTotal = 20, TotalQuantity = (int)Math.Min(int.MaxValue, quantities.Sum(q => (long)q)), LastStatus = cancelled ? OrderStatus.Cancelled : OrderStatus.New });
            await db.SaveChangesAsync(); await db.GetService<IMigrator>().MigrateAsync("20261003154113_AddSafeOrderNumbers");
        }
        var source = Path.Combine(Path.GetDirectoryName(f.Paths.DatabasePath)!, "pre-identity-evidence.db"); File.Copy(f.Paths.DatabasePath, source);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await LegacyReviewEvidence.Prepare(f.Paths.DatabasePath, source, hash);
        await using (var db = f.Db()) await db.Database.MigrateAsync();
        return new(f, p, id, source, hash);
    }
    static async Task<InventoryReviewPreview> Preview(Scenario s, OrderStatus status, Guid? target = null)
    {
        await using var db = s.F.Db(); var service = s.F.Orders(db);
        var need = await service.GetInventoryReviewAsync(s.OrderId, status, s.F.Token);
        return await service.PreviewInventoryReviewAsync(s.OrderId, status,
            need.Items.ToDictionary(i => i.OrderItemId, i => target ?? s.P.Sizes[0].Id), s.F.Token);
    }
    static ConfirmInventoryReview Command(InventoryReviewPreview p, string reason = "مراجعة اصطناعية مؤكدة") => new(p.OperationId, p.OrderId, p.ProtectedTicket, reason);
    static async Task<InventoryReviewResult> Confirm(Scenario s, InventoryReviewPreview p)
    { await using var db = s.F.Db(); return await s.F.Orders(db).ConfirmInventoryReviewAsync(Command(p), s.F.Token); }
    static async Task Unchanged(Scenario s, int stock = 3, OrderStatus status = OrderStatus.New)
    {
        await using var db = s.F.Db(); Check(await s.F.Quantity(s.P) == stock && (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == s.OrderId)).Status == status);
        Check(await db.OrderInventoryReviews.CountAsync() == 0 && await db.OrderItems.AllAsync(i => i.ProductSizeId == null));
    }
    static async Task<int> Race(params Func<Task>[] actions)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var tasks = actions.Select(a => Task.Run(async () => { if (Interlocked.Increment(ref count) == actions.Length) ready.SetResult();
            await start.Task; try { await a(); return 1; } catch (InvalidOperationException) { return 0; } })).ToArray();
        await ready.Task; start.SetResult(); return (await Task.WhenAll(tasks)).Sum();
    }
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Func<Task> action) => tests.Add(("review " + name, action));
        Test("01 migration evidence preserves original rows null identities finances stock and source hash", async () =>
        {
            var s = await Historical(); await Unchanged(s); await using var db = s.F.Db();
            var item = await db.OrderItems.SingleAsync(); var evidence = await db.HistoricalOrderItemEvidence.SingleAsync();
            Check(evidence.ItemFingerprint == HistoricalItemFingerprint.Create(item) && evidence.SourceSha256 == s.SourceHash);
            Check(await s.F.Quantity(s.P, "M") == 4 && await db.OrderNumberReservations.CountAsync() == 1 && await db.OrderNumberAudits.CountAsync() == 0);
            Check(s.SourceHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(s.SourcePath))));
        });
        Test("02 preview then close makes no movement mapping status or success audit", async () =>
        { var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); Check(p.Movements.Single().StockAfter == 5); await Unchanged(s); });
        Test("03 confirmed cancellation restores explicit zero/unpublished target once and keeps purchase snapshots", async () =>
        {
            var s = await Historical(); await using var db = s.F.Db();
            await db.Products.ExecuteUpdateAsync(x => x.SetProperty(p => p.IsPublished, false));
            await db.ProductSizes.Where(x => x.Id == s.P.Sizes[1].Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Quantity, 0));
            var before = await db.OrderItems.AsNoTracking().SingleAsync(); var fingerprint = HistoricalItemFingerprint.Create(before);
            var p = await Preview(s, OrderStatus.Cancelled, s.P.Sizes[1].Id); Check(p.Movements.Single().StockBefore == 0 && p.Movements.Single().StockAfter == 2);
            await Confirm(s, p); Check(await s.F.Quantity(s.P) == 3 && await s.F.Quantity(s.P, "M") == 2 && !await db.Products.AnyAsync(p => p.IsPublished));
            Check(HistoricalItemFingerprint.Create(await db.OrderItems.AsNoTracking().SingleAsync()) == fingerprint && await db.OrderItems.AllAsync(i => i.ProductSizeId == null));
            Check((await db.OrderInventoryReviews.Include(r => r.Lines).SingleAsync()).Lines.Single().TargetSizeId == s.P.Sizes[1].Id);
        });
        Test("04 pre-upgrade cancelled revival 3->1; insufficient and overflow reject atomically", async () =>
        {
            var s = await Historical(cancelled: true); var p = await Preview(s, OrderStatus.New); Check(p.Movements.Single().StockAfter == 1); await Confirm(s, p);
            var low = await Historical(cancelled: true, stock: 1); await Reject(() => Preview(low, OrderStatus.New)); await Unchanged(low, 1, OrderStatus.Cancelled);
            var overflow = await Historical(stock: int.MaxValue); await Reject(() => Preview(overflow, OrderStatus.Cancelled)); await Unchanged(overflow, int.MaxValue);
        });
        Test("05 one matching candidate never substitutes for explicit selection and confirmation", async () =>
        {
            var s = await Historical(); await using var db = s.F.Db(); await db.ProductSizes.Where(p => p.Id == s.P.Sizes[1].Id).ExecuteDeleteAsync();
            var need = await s.F.Orders(db).GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, s.F.Token); Check(need.Items.Single().Candidates.Count == 1);
            await Reject(() => s.F.Orders(db).PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, new Dictionary<Guid, Guid>(), s.F.Token)); await Unchanged(s);
        });
        Test("06 foreign product/line extra choices ticket operation/order tampering and unauthorized entries reject", async () =>
        {
            var s = await Historical(); await using var db = s.F.Db(); var service = s.F.Orders(db); var other = await s.F.Product(3);
            var need = await service.GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, s.F.Token); var line = need.Items.Single().OrderItemId;
            await Reject(() => service.PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, new Dictionary<Guid, Guid> { [line] = other.Sizes[0].Id }, s.F.Token));
            await Reject(() => service.PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, new Dictionary<Guid, Guid> { [Guid.NewGuid()] = s.P.Sizes[0].Id }, s.F.Token));
            var p = await Preview(s, OrderStatus.Cancelled); var command = Command(p);
            await Reject(() => service.ConfirmInventoryReviewAsync(command with { ProtectedTicket = "tampered" }, s.F.Token));
            await Reject(() => service.ConfirmInventoryReviewAsync(command with { OrderId = Guid.NewGuid() }, s.F.Token));
            await Reject(() => service.ConfirmInventoryReviewAsync(command with { OperationId = Guid.NewGuid() }, s.F.Token));
            // Simulate a malformed server-signed payload too: recomputation must still win over
            // supplied quantity, stock, status or admin. Browsers never have these protection keys.
            var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(s.F.Paths.DatabasePath)!, "review-test-keys")))
                .CreateProtector("LaraFashion.OrderInventoryReview.V1");
            foreach (var mutation in new[] { "quantity", "stock", "status", "admin" })
            {
                var payload = JsonNode.Parse(protector.Unprotect(p.ProtectedTicket))!;
                if (mutation == "quantity") payload["Items"]![0]!["Line"]!["Quantity"] = 999;
                else if (mutation == "stock") payload["Movements"]![0]!["StockAfter"] = 999;
                else if (mutation == "status") payload["NewStatus"] = (int)OrderStatus.Preparing;
                else payload["AdminIdentity"] = "forged-admin";
                await Reject(() => service.ConfirmInventoryReviewAsync(command with { ProtectedTicket = protector.Protect(payload.ToJsonString()) }, s.F.Token));
            }
            await Unauthorized(() => service.GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, "invalid"));
            await Unauthorized(() => service.PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, new Dictionary<Guid, Guid>(), "invalid"));
            await Unauthorized(() => service.ConfirmInventoryReviewAsync(command, "invalid"));
            await Unauthorized(() => service.GetInventoryReviewAuditAsync(s.OrderId, "invalid"));
            await Unchanged(s);
        });
        Test("07 several historical and modern lines sharing target aggregate once; no double restore", async () =>
        {
            var s = await Historical(quantities: [2, 1]); await using var db = s.F.Db();
            db.OrderItems.Add(new() { Id = Guid.NewGuid(), OrderId = s.OrderId, ProductId = s.P.Id, ProductSizeId = s.P.Sizes[0].Id, ProductName = s.P.Name, Size = "S", Quantity = 2, UnitPrice = 10 }); await db.SaveChangesAsync();
            var p = await Preview(s, OrderStatus.Cancelled); Check(p.Movements.Single().Quantity == 5 && p.Movements.Single().StockAfter == 8 && p.Lines.Count == 3);
            await Confirm(s, p); Check(await s.F.Quantity(s.P) == 8 && await db.OrderInventoryReviewLines.CountAsync() == 3 && await db.OrderItems.CountAsync(i => i.ProductSizeId == null) == 2);
        });
        Test("08 incomplete choice and later target failure have no partial effects; grouped overflow rejects", async () =>
        {
            var s = await Historical(quantities: [2, 1]); await using var db = s.F.Db(); var service = s.F.Orders(db);
            var need = await service.GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, s.F.Token);
            await Reject(() => service.PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled,
                new Dictionary<Guid, Guid> { [need.Items[0].OrderItemId] = s.P.Sizes[0].Id }, s.F.Token)); await Unchanged(s);
            var many = await Historical(quantities: [int.MaxValue, 1]); await Reject(() => Preview(many, OrderStatus.Cancelled)); await Unchanged(many);
        });
        Test("09 double confirm lost-response replay fresh services durable keys same ID changed content rejects", async () =>
        {
            var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled);
            Check(await Race(async () => await Confirm(s, p), async () => await Confirm(s, p)) == 2);
            await using var db = s.F.Db(); var service = s.F.Orders(db);
            Check((await service.ConfirmInventoryReviewAsync(Command(p), s.F.Token)).Replayed);
            var requestFile = Path.Combine(Path.GetDirectoryName(s.F.Paths.DatabasePath)!, "synthetic-review-retry.json");
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(Command(p)));
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(HistoricalInventoryReviewTests).Assembly.Location); start.ArgumentList.Add("--retry-review-fixture");
            start.ArgumentList.Add(s.F.Paths.DatabasePath); start.ArgumentList.Add(requestFile);
            using var child = Process.Start(start)!; var output = child.StandardError.ReadToEndAsync(); await child.WaitForExitAsync();
            Check(child.ExitCode == 0, await output);
            await Reject(() => service.ConfirmInventoryReviewAsync(Command(p, "different"), s.F.Token));
            await Unauthorized(() => service.ConfirmInventoryReviewAsync(Command(p), "invalid"));
            Check(await s.F.Quantity(s.P) == 5 && await db.OrderInventoryReviews.CountAsync() == 1 && (await db.Orders.SingleAsync()).InventoryRevision == 1);
        });
        Test("10 distinct concurrent confirmations and ordinary/bulk contenders cannot apply same source twice", async () =>
        {
            var s = await Historical(); var a = await Preview(s, OrderStatus.Cancelled); await using var second = s.F.Db();
            var secondToken = new JwtService(s.F.Configuration).GenerateAdminToken("second-test-admin"); var secondService = s.F.Orders(second);
            var need = await secondService.GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, secondToken);
            var b = await secondService.PreviewInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, need.Items.ToDictionary(i => i.OrderItemId, i => s.P.Sizes[0].Id), secondToken);
            Check(await Race(async () => await Confirm(s, a), async () => await secondService.ConfirmInventoryReviewAsync(Command(b), secondToken)) == 1 && await s.F.Quantity(s.P) == 5);
            var t = await Historical(); var p = await Preview(t, OrderStatus.Cancelled);
            Check(await Race(async () => await Confirm(t, p), async () => { await using var db = t.F.Db(); await t.F.Orders(db).UpdateStatusesAsync([t.OrderId], OrderStatus.Preparing, t.F.Token,
                new Dictionary<Guid, long> { [t.OrderId] = 0 }); }) == 1);
            await using var check = t.F.Db(); Check(await check.OrderInventoryReviews.CountAsync() <= 1);
            var u = await Historical(); var up = await Preview(u, OrderStatus.Cancelled);
            Check(await Race(async () => await Confirm(u, up), async () => { await using var db = u.F.Db(); await u.F.Orders(db).UpdateStatusAsync(u.OrderId, OrderStatus.Preparing, u.F.Token, 0); }) == 1);
        });
        Test("11 stock purchase/admin edit or rename/delete after preview invalidates confirmation", async () =>
        {
            foreach (var change in new[] { "purchase", "stock", "rename", "delete" })
            {
                var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); await using var db = s.F.Db();
                if (change == "purchase") await s.F.Buy(s.P);
                else if (change == "stock") await db.ProductSizes.Where(x => x.Id == s.P.Sizes[0].Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Quantity, 4));
                else if (change == "rename") { var size = await db.ProductSizes.SingleAsync(x => x.Id == s.P.Sizes[0].Id); size.SizeName = "L"; await db.SaveChangesAsync(); }
                else await db.ProductSizes.Where(x => x.Id == s.P.Sizes[0].Id).ExecuteDeleteAsync();
                await Reject(() => Confirm(s, p)); Check((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == s.OrderId)).Status == OrderStatus.New && await db.OrderInventoryReviews.CountAsync() == 0);
            }
        });
        Test("12 status ABA invalidates old preview even when source status returns; revision separate from number", async () =>
        {
            var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); await using var db = s.F.Db(); var service = s.F.Orders(db);
            await service.UpdateStatusAsync(s.OrderId, OrderStatus.Preparing, s.F.Token); await service.UpdateStatusAsync(s.OrderId, OrderStatus.New, s.F.Token);
            await Reject(() => Confirm(s, p)); var o = await db.Orders.SingleAsync(); Check(o.InventoryRevision == 2 && o.NumberRevision == 0); await Unchanged(s);
        });
        Test("13 cancel revive cancel links operations and locks same target; replay never rolls current state back", async () =>
        {
            var s = await Historical(); var cancel = await Preview(s, OrderStatus.Cancelled); await Confirm(s, cancel);
            var revive = await Preview(s, OrderStatus.New); await Confirm(s, revive); Check(await s.F.Quantity(s.P) == 3);
            await using var db = s.F.Db(); var service = s.F.Orders(db); Check((await service.ConfirmInventoryReviewAsync(Command(cancel), s.F.Token)).Replayed);
            Check((await db.Orders.AsNoTracking().SingleAsync()).Status == OrderStatus.New && await s.F.Quantity(s.P) == 3);
            await Reject(() => Preview(s, OrderStatus.Cancelled, s.P.Sizes[1].Id)); await Confirm(s, await Preview(s, OrderStatus.Cancelled));
            var audit = await service.GetInventoryReviewAuditAsync(s.OrderId, s.F.Token);
            Check(audit.Count == 3 && audit[1].PreviousOperationId == audit[0].OperationId && audit[2].PreviousOperationId == audit[1].OperationId && await s.F.Quantity(s.P) == 5);
        });
        Test("14 failure saving header detail movement or status rolls back inventory history revision audit", async () =>
        {
            foreach (var table in new[] { "OrderInventoryReviews", "OrderInventoryReviewLines", "OrderInventoryReviewMovements", "Orders", "SalesHistory" })
            {
                var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); await using var db = s.F.Db();
                var verb = table is "Orders" or "SalesHistory" ? "UPDATE" : "INSERT";
                // Table/verb come solely from the fixed test matrix above; never user input.
                var failureSql = $"CREATE TRIGGER ReviewTestFail BEFORE {verb} ON {table} BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;";
                await db.Database.ExecuteSqlRawAsync(failureSql);
                await Reject(() => Confirm(s, p)); await Unchanged(s);
                Check((await db.Orders.AsNoTracking().SingleAsync()).InventoryRevision == 0 && (await db.SalesHistory.AsNoTracking().SingleAsync()).LastStatus == OrderStatus.New);
            }
        });
        Test("15 missing product replacement same name or no size blocks; never creates alternative", async () =>
        {
            foreach (var change in new[] { "product", "sizes" })
            {
                var s = await Historical(); await using var db = s.F.Db();
                if (change == "product") { await db.Products.ExecuteDeleteAsync(); await s.F.Product(3); }
                else await db.ProductSizes.ExecuteDeleteAsync();
                var need = await s.F.Orders(db).GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, s.F.Token); Check(need.Items.All(i => i.BlockedReason is not null));
                await Reject(() => Preview(s, OrderStatus.Cancelled)); Check((await db.Orders.SingleAsync()).Status == OrderStatus.New && await db.OrderInventoryReviews.CountAsync() == 0);
            }
        });
        Test("16 no-inventory transitions and current state no-op need no review and update revisions correctly", async () =>
        {
            var s = await Historical(); await using var db = s.F.Db(); var service = s.F.Orders(db);
            await service.UpdateStatusAsync(s.OrderId, OrderStatus.New, s.F.Token);
            await service.UpdateStatusAsync(s.OrderId, OrderStatus.Ready, s.F.Token); await service.UpdateStatusAsync(s.OrderId, OrderStatus.Ready, s.F.Token);
            Check((await db.Orders.SingleAsync()).InventoryRevision == 1 && await s.F.Quantity(s.P) == 3 && await db.OrderInventoryReviews.CountAsync() == 0);
            await Unauthorized(() => service.UpdateStatusAsync(s.OrderId, OrderStatus.New, "invalid"));
        });
        Test("17 mixed bulk refuses all and returns structured needs; later no-op and individual review work", async () =>
        {
            var s = await Historical(); var modern = await s.F.Buy(s.P); await using var db = s.F.Db(); var service = s.F.Orders(db);
            try { await service.UpdateStatusesAsync([s.OrderId, modern.Id], OrderStatus.Cancelled, s.F.Token); throw new Exception("Expected review need"); }
            catch (InventoryReviewRequiredException ex) { Check(ex.Orders.Count == 1 && ex.Orders[0].OrderId == s.OrderId); }
            Check(await db.Orders.AllAsync(o => o.Status == OrderStatus.New) && await s.F.Quantity(s.P) == 2);
            await Confirm(s, await Preview(s, OrderStatus.Cancelled));
            Check((await service.UpdateStatusesAsync([s.OrderId, modern.Id], OrderStatus.Cancelled, s.F.Token)).UpdatedOrders == 1 && await s.F.Quantity(s.P) == 5);
        });
        Test("18 cleanup delete destinations preserve audit evidence replay and immutable DB guards", async () =>
        {
            var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); await Confirm(s, p); await using var db = s.F.Db();
            await db.Orders.ExecuteUpdateAsync(o => o.SetProperty(x => x.UpdatedAt, DateTime.Now.AddDays(-31)));
            Check((await s.F.Orders(db).CleanOldOrdersAsync(s.F.Token)).DeletedOrders == 1);
            await s.F.Products(db).DeleteProductAsync(s.P.Id);
            Check(await db.OrderInventoryReviews.CountAsync() == 1 && await db.OrderInventoryReviewLines.CountAsync() == 1 && await db.HistoricalOrderItemEvidence.CountAsync() == 1);
            Check((await Confirm(s, p)).Replayed);
            try { await db.OrderInventoryReviews.ExecuteDeleteAsync(); throw new Exception("Expected guard"); } catch (SqliteException) { }
            try { await db.GetService<IMigrator>().MigrateAsync("20261005162501_AddStableProductSizeIdentity"); throw new Exception("Expected retained audit guard"); } catch (SqliteException) { }
            var deleted = await Historical(); await Confirm(deleted, await Preview(deleted, OrderStatus.Cancelled)); await using var other = deleted.F.Db();
            await deleted.F.Orders(other).DeleteOrderAsync(deleted.OrderId);
            Check(await other.OrderInventoryReviews.CountAsync() == 1 && await other.OrderInventoryReviewLines.CountAsync() == 1);
            var missing = await Historical(); await Confirm(missing, await Preview(missing, OrderStatus.Cancelled));
            await using var live = missing.F.Db(); await missing.F.Products(live).DeleteProductAsync(missing.P.Id);
            await using var diagnosticDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = missing.F.Paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await diagnosticDb.OpenAsync(); var originalOutput = Console.Out; using var captured = new StringWriter();
            try { Console.SetOut(captured); await ReviewDiagnostics.Run(diagnosticDb); } finally { Console.SetOut(originalOutput); }
            using var diagnostic = JsonDocument.Parse(captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
            var row = diagnostic.RootElement.GetProperty("Rows")[0];
            Check(row.GetProperty("MissingProduct").GetInt32() == 1 && row.GetProperty("PreviouslyReviewed").GetInt32() == 1
                && row.GetProperty("UnreviewedEligibleLines").GetInt32() == 0);
        });
        Test("19 sandbox isolation and official financial snapshot order number and original timestamps preserved", async () =>
        {
            foreach (var sandbox in new[] { false, true })
            {
                var s = await Historical(sandbox: sandbox); await using var db = s.F.Db(); var before = await db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync();
                var history = await db.SalesHistory.AsNoTracking().SingleOrDefaultAsync(); var p = await Preview(s, OrderStatus.Cancelled); await Confirm(s, p);
                var after = await db.Orders.AsNoTracking().Include(o => o.Items).SingleAsync();
                Check(before.CreatedAt == after.CreatedAt && before.OrderNumber == after.OrderNumber && before.FinalTotal == after.FinalTotal
                    && before.NumberRevision == after.NumberRevision && JsonSerializer.Serialize(before.Items) == JsonSerializer.Serialize(after.Items));
                Check((await db.OrderInventoryReviews.SingleAsync()).IsSandbox == sandbox);
                if (sandbox) Check(await db.SalesHistory.CountAsync() == 0);
                else { var updated = await db.SalesHistory.AsNoTracking().SingleAsync(); Check(updated.FinalTotal == history!.FinalTotal && updated.CreatedAtUtc == history.CreatedAtUtc && updated.LastStatus == OrderStatus.Cancelled); }
            }
        });
        Test("20 blank reason never executes and closing/reopening has no saved selection; server protection has no editable quantity/admin", async () =>
        {
            var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled); await using var db = s.F.Db();
            await Reject(() => s.F.Orders(db).ConfirmInventoryReviewAsync(Command(p, " "), s.F.Token)); await Unchanged(s);
            var reopened = await s.F.Orders(db).GetInventoryReviewAsync(s.OrderId, OrderStatus.Cancelled, s.F.Token);
            Check(reopened.Items.Single().PreviousTargetId is null && await db.OrderInventoryReviews.CountAsync() == 0);
        });
        Test("21 new null corruption and damaged modern IDs cannot use historical bypass; original evidence mutations block", async () =>
        {
            var f = await Fixture.Create(); var product = await f.Product(3); var order = await f.Buy(product); await using var db = f.Db();
            await db.OrderItems.ExecuteUpdateAsync(i => i.SetProperty(x => x.ProductSizeId, (Guid?)null));
            var need = await f.Orders(db).GetInventoryReviewAsync(order.Id, OrderStatus.Cancelled, f.Token);
            Check(need.Items.Single().BlockedReason is not null);
            db.HistoricalOrderItemEvidence.Add(new() { OrderItemId = (await db.OrderItems.SingleAsync()).Id, OrderId = order.Id, ProductId = product.Id,
                ItemFingerprint = HistoricalItemFingerprint.Create(await db.OrderItems.SingleAsync()), SourceSha256 = "FORGED", SourceMigration = "FORGED", RegisteredAtUtc = DateTime.UtcNow });
            try { await db.SaveChangesAsync(); throw new Exception("Expected closed evidence boundary"); } catch (DbUpdateException) { }
            await Reject(() => f.Orders(db).PreviewInventoryReviewAsync(order.Id, OrderStatus.Cancelled, need.Items.ToDictionary(i => i.OrderItemId, i => product.Sizes[0].Id), f.Token));
            var s = await Historical(); await using var old = s.F.Db(); await old.OrderItems.ExecuteUpdateAsync(i => i.SetProperty(x => x.Quantity, 3));
            await Reject(() => Preview(s, OrderStatus.Cancelled)); Check(await s.F.Quantity(s.P) == 3);
        });
        Test("22 deleted or semantically renamed prior destination blocks next cycle rather than transferring stock", async () =>
        {
            foreach (var delete in new[] { false, true })
            {
                var s = await Historical(); await Confirm(s, await Preview(s, OrderStatus.Cancelled)); await using var db = s.F.Db();
                if (delete) await db.ProductSizes.Where(x => x.Id == s.P.Sizes[0].Id).ExecuteDeleteAsync();
                else { var size = await db.ProductSizes.SingleAsync(x => x.Id == s.P.Sizes[0].Id); size.SizeName = "L"; await db.SaveChangesAsync(); }
                var need = await s.F.Orders(db).GetInventoryReviewAsync(s.OrderId, OrderStatus.New, s.F.Token); Check(need.Items.Single().BlockedReason is not null);
                await Reject(() => Preview(s, OrderStatus.New, s.P.Sizes[1].Id)); Check((await db.Orders.SingleAsync()).Status == OrderStatus.Cancelled && await db.OrderInventoryReviews.CountAsync() == 1);
            }
        });
        Test("23 already identity-upgraded environment requires hash verified pre-identity proof; wrong hash/source reject", async () =>
        {
            var f = await Fixture.Create(previous: true); await f.Product(3); await f.SeedLegacy("EVIDENCE-23", false);
            await using (var db = f.Db()) await db.GetService<IMigrator>().MigrateAsync("20261003154113_AddSafeOrderNumbers");
            var source = Path.Combine(Path.GetDirectoryName(f.Paths.DatabasePath)!, "old-evidence.db"); File.Copy(f.Paths.DatabasePath, source);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            await using (var db = f.Db()) await db.GetService<IMigrator>().MigrateAsync("20261005162501_AddStableProductSizeIdentity");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(f.Paths.DatabasePath));
            await Reject(() => LegacyReviewEvidence.Prepare(f.Paths.DatabasePath, source, "WRONG"));
            Check(before.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(f.Paths.DatabasePath))));
            Check(await LegacyReviewEvidence.Prepare(f.Paths.DatabasePath, source, hash) == 1);
            await using var migrated = f.Db(); await migrated.Database.MigrateAsync(); Check(await migrated.HistoricalOrderItemEvidence.CountAsync() == 1 && await migrated.OrderItems.AllAsync(i => i.ProductSizeId == null));
            await Reject(() => LegacyReviewEvidence.Prepare(f.Paths.DatabasePath, f.Paths.DatabasePath, hash));
        });
        Test("24 stock/name ABA invalidates preview by persistent destination revision", async () =>
        {
            foreach (var rename in new[] { false, true })
            {
                var s = await Historical(); var p = await Preview(s, OrderStatus.Cancelled);
                foreach (var back in new[] { false, true })
                {
                    await using var db = s.F.Db();
                    if (rename) { var size = await db.ProductSizes.SingleAsync(x => x.Id == s.P.Sizes[0].Id); size.SizeName = back ? "S" : "L"; await db.SaveChangesAsync(); }
                    else await db.ProductSizes.Where(x => x.Id == s.P.Sizes[0].Id).ExecuteUpdateAsync(x => x.SetProperty(q => q.Quantity, back ? 3 : 4));
                }
                await Reject(() => Confirm(s, p)); await Unchanged(s);
                await using var fresh = s.F.Db(); Check((await fresh.ProductSizes.SingleAsync(x => x.Id == s.P.Sizes[0].Id)).ReviewRevision == 2);
            }
        });
    }
}
