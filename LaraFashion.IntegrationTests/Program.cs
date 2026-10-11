using LaraFashion.Models;
using LaraFashion.Models.Enums;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--test-linux-acceptance")
    return await LinuxAcceptance.Run(args);
if (args.Length>0 && args[0]=="--test-deployment-harness")
{ try { return await DeploymentHarness.Run(args); } catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { Success=false,FailureCode=ex is DeploymentFailure failure ? failure.Code : ex.GetType().Name })); return 2; } }
if (DeploymentMaintenance.Handles(args)) return await DeploymentMaintenance.Run(args);
if (args.Length == 2 && args[0] == "--diagnose") return await Diagnostics.Run(args[1]);
if (args.Length == 1 && args[0] == "--ui") return await UiHost.Run();
if (args.Length == 1 && args[0] == "--reproduce-sizes") return await SizeReproduction.Run();
if (args.Length == 4 && args[0] == "--prepare-legacy-review")
{ await LegacyReviewEvidence.Prepare(args[1], args[2], args[3]); return 0; }
if (args.Length == 3 && args[0] == "--verify-review-upgrade") return await ReviewUpgradeVerification.Run(args[1], args[2]);
if (args.Length == 3 && args[0] == "--retry-review-fixture")
{
    var replayFixture = Fixture.OpenExistingSynthetic(args[1]);
    if (Path.GetDirectoryName(Path.GetFullPath(args[2])) != Path.GetDirectoryName(replayFixture.Paths.DatabasePath)) throw new InvalidOperationException("Fixture request path mismatch.");
    var request = JsonSerializer.Deserialize<ConfirmInventoryReview>(await File.ReadAllTextAsync(args[2]))!;
    await using var replayDb = replayFixture.Db();
    var result = await replayFixture.Orders(replayDb).ConfirmInventoryReviewAsync(request, replayFixture.Token);
    return result.Replayed ? 0 : 1;
}
if (args.Length != 0)
{ Console.WriteLine("{\"ContractVersion\":1,\"Success\":false,\"FailureCode\":\"InvalidArguments\"}"); return 2; }
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Fixture, Task> run) => tests.Add((name, async () => { var f = await Fixture.Create(); await run(f); }));
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Reject<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task<int> Race(params Func<Task>[] actions)
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
    var tasks = actions.Select(action => Task.Run(async () =>
    {
        if (Interlocked.Increment(ref count) == actions.Length) ready.SetResult(); await gate.Task;
        try { await action(); return 1; } catch (InvalidOperationException) { return 0; }
    })).ToArray();
    await ready.Task; gate.SetResult(); return (await Task.WhenAll(tasks)).Sum();
}
Test("01 S exhausted leaves M=3 L=4 visible with original size IDs", async f =>
{
    var p = await f.Product(2, 3, 4); await f.Buy(p, "S", 2); await using var db = f.Db();
    var fresh = (await f.Products(db).GetActiveProductsAsync()).Single();
    Check(fresh.IsPublished && (await f.Quantities(p)).SequenceEqual(new[] { 0, 3, 4 }));
    Check(fresh.Sizes.Select(x => x.Id).ToHashSet().SetEquals(p.Sizes.Select(x => x.Id)));
});
Test("02-03 partial sequential sizes; sold out hidden and retained for admin", async f =>
{
    var p = await f.Product(2, 3, 4);
    foreach (var step in new[] { ("S", 1), ("M", 2), ("L", 3), ("S", 1), ("M", 1) })
    { await f.Buy(p, step.Item1, step.Item2); await using var db = f.Db(); Check((await f.Products(db).GetActiveProductsAsync()).Count == 1); }
    await f.Buy(p, "L", 1); await using var check = f.Db();
    Check((await f.Products(check).GetActiveProductsAsync()).Count == 0);
    Check((await f.Products(check).GetAllProductsAsync()).Single().IsPublished);
});
Test("04 concurrent last piece: one order and no negative stock", async f =>
{
    var p = await f.Product(1); Check(await Race(async () => await f.Buy(p), async () => await f.Buy(p)) == 1);
    await using var db = f.Db(); Check(await db.Orders.CountAsync() == 1 && await f.Quantity(p) == 0);
});
Test("05 concurrent different sizes preserve both decrements", async f =>
{
    var p = await f.Product(1, 1); Check(await Race(async () => await f.Buy(p), async () => await f.Buy(p, "M")) == 2);
    await using var db = f.Db(); Check(await db.Orders.CountAsync() == 2 && await db.ProductSizes.AllAsync(x => x.Quantity == 0));
});
Test("06 duplicate lines aggregate and invalid quantities rejected", async f =>
{
    var p = await f.Product(3); await using var db = f.Db(); var orders = f.Orders(db);
    await Reject<InvalidOperationException>(() => orders.CreateOrderAsync(f.Customer(), [f.Item(p, q: 2), f.Item(p, q: 2)], f.Total(4)));
    Check(await f.Quantity(p) == 3 && await db.Orders.CountAsync() == 0);
    await orders.CreateOrderAsync(f.Customer(), [f.Item(p), f.Item(p, q: 2)], f.Total(3)); Check(await f.Quantity(p) == 0);
    await Reject<InvalidOperationException>(() => orders.CreateOrderAsync(f.Customer(), [f.Item(p, q: -1)], f.Total()));
});
Test("07 rollback on later-line and history insert failure", async f =>
{
    var p = await f.Product(2, 0); await using var db = f.Db();
    await Reject<InvalidOperationException>(() => f.Orders(db).CreateOrderAsync(f.Customer(), [f.Item(p), f.Item(p, "M")], f.Total(2)));
    Check(await f.Quantity(p) == 2);
    await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailHistory BEFORE INSERT ON SalesHistory BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
    await Reject<InvalidOperationException>(async () => await f.Buy(p));
    Check(await f.Quantity(p) == 2 && await db.Orders.CountAsync() == 0 && await db.OrderNumberReservations.CountAsync() == 0);
});
Test("08 concurrent cancellation and revival once; failed bulk atomic", async f =>
{
    var p = await f.Product(2); var o = await f.Buy(p, q: 2);
    async Task Status(OrderStatus s) { await using var db = f.Db(); await f.Orders(db).UpdateStatusAsync(o.Id, s, f.Token); }
    Check(await Race(() => Status(OrderStatus.Cancelled), () => Status(OrderStatus.Cancelled)) == 2 && await f.Quantity(p) == 2);
    Check(await Race(() => Status(OrderStatus.New), () => Status(OrderStatus.New)) == 2 && await f.Quantity(p) == 0);
    await Status(OrderStatus.Cancelled); var other = await f.Buy(p, q: 2); await using var db = f.Db();
    await Reject<InvalidOperationException>(() => f.Orders(db).UpdateStatusesAsync([o.Id, other.Id], OrderStatus.New, f.Token));
    Check((await f.Orders(db).GetOrderAsync(o.Id))!.Status == OrderStatus.Cancelled && await f.Quantity(p) == 0);
});
Test("09 stale cleanup cannot delete restored stock; admin deletion and open order rules", async f =>
{
    var p = await f.Product(0, 0); await using var stale = f.Db(); await stale.Products.Include(x => x.Sizes).LoadAsync();
    await using (var other = f.Db()) await other.ProductSizes.Where(x => x.ProductId == p.Id && x.SizeName == "M").ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, 3));
    Check((await f.Products(stale).CleanDeletableProductsAsync()).DeletedProducts == 0 && await f.Quantity(p, "M") == 3);
    var o = await f.Buy(p, "M"); await Reject<InvalidOperationException>(() => f.Products(stale).DeleteProductAsync(p.Id));
    await f.Orders(stale).UpdateStatusAsync(o.Id, OrderStatus.Ready, f.Token); await f.Products(stale).DeleteProductAsync(p.Id);
    Check(!await stale.Products.AsNoTracking().AnyAsync(x => x.Id == p.Id));
});
Test("09b old order cleanup retains product stock history and reservations", async f =>
{
    var p = await f.Product(3); var o = await f.Buy(p); await using var db = f.Db(); await f.Orders(db).UpdateStatusAsync(o.Id, OrderStatus.Ready, f.Token);
    await db.Orders.Where(x => x.Id == o.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTime.Now.AddDays(-31)));
    Check((await f.Orders(db).CleanOldOrdersAsync(f.Token)).DeletedOrders == 1);
    Check(await f.Quantity(p) == 2 && await db.Products.CountAsync() == 1 && await db.SalesHistory.CountAsync() == 1 && await db.OrderNumberReservations.CountAsync() == 1);
});
Test("10 fresh current and other circuit queries; restart; preserve unpublished intent", async f =>
{
    var p = await f.Product(1, 1); var unpublished = await f.Product(3); await using var current = f.Db(); await using var other = f.Db();
    await current.ProductSizes.LoadAsync(); await other.ProductSizes.LoadAsync();
    await other.Products.Where(x => x.Id == unpublished.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPublished, false)); await f.Buy(p);
    foreach (var db in new[] { current, other })
    { var fresh = await f.Products(db).GetActiveProductsAsync(); Check(fresh.Count == 1 && fresh[0].Sizes.Single(x => x.SizeName == "S").Quantity == 0); }
    await f.Buy(p, "M"); await using var restarted = f.Db(); Check((await f.Products(restarted).GetActiveProductsAsync()).Count == 0 && await restarted.Products.CountAsync() == 2);
});
Test("positive quantity with invalid empty size is hidden without changing data", async f =>
{
    await f.Product(3); await using var db = f.Db();
    await Reject<SqliteException>(() => db.ProductSizes.ExecuteUpdateAsync(s => s.SetProperty(x => x.SizeName, " ")));
    // Simulate pre-existing corruption solely on this disposable fixture to retain the read-path check.
    await db.Database.ExecuteSqlRawAsync("DROP TRIGGER ProductSizeValidBeforeUPDATE");
    await db.ProductSizes.ExecuteUpdateAsync(s => s.SetProperty(x => x.SizeName, " "));
    Check((await f.Products(db).GetActiveProductsAsync()).Count == 0
        && (await f.Products(db).GetAllProductsAsync()).Single().IsPublished
        && await db.ProductSizes.SumAsync(x => x.Quantity) == 3);
});
Test("11 concurrent independent checkouts distinct GUID and display number", async f =>
{
    var p = await f.Product(12); Check(await Race(Enumerable.Range(0, 8).Select(_ => (Func<Task>)(async () => await f.Buy(p))).ToArray()) == 8);
    await using var db = f.Db(); var saved = await db.Orders.ToListAsync();
    Check(saved.Count == 8 && saved.Select(x => x.Id).Distinct().Count() == 8 && saved.Select(x => x.OrderNumber).Distinct().Count() == 8 && await f.Quantity(p) == 4);
});
Test("12 concurrent same attempt; retry; independent attempts allowed", async f =>
{
    var p = await f.Product(5); var id = Guid.NewGuid(); var saved = new System.Collections.Concurrent.ConcurrentBag<Guid>();
    Check(await Race(async () => saved.Add((await f.Buy(p, attempt: id)).Id), async () => saved.Add((await f.Buy(p, attempt: id)).Id)) == 2);
    Check(saved.Distinct().Count() == 1 && await f.Quantity(p) == 4 && (await f.Buy(p, attempt: id)).Id == saved.First());
    await using var db = f.Db(); Check((await f.Orders(db).GetCheckoutReceiptAsync(id))!.OrderId == saved.First());
    await Reject<InvalidOperationException>(async () => await f.Buy(p, q: 2, attempt: id)); await f.Buy(p, attempt: Guid.NewGuid());
    Check(await db.Orders.CountAsync() == 2 && await f.Quantity(p) == 3);
});
Test("13 deleted identities and previous numbers never reallocated", async f =>
{
    var p = await f.Product(8); var attempt = Guid.NewGuid(); var o = await f.Buy(p, attempt: attempt); await using var db = f.Db(); var service = f.Orders(db);
    await service.EditOrderNumberAsync(o.Id, "CUSTOM-13", 0, "Test", f.Token); await service.UpdateStatusAsync(o.Id, OrderStatus.Ready, f.Token); await service.DeleteOrderAsync(o.Id);
    var next = await f.Buy(p); Check(next.Id != o.Id && next.OrderNumber != o.OrderNumber);
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(next.Id, o.OrderNumber, 0, "Test", f.Token));
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(next.Id, "CUSTOM-13", 0, "Test", f.Token));
    Check((await service.GetCheckoutReceiptAsync(attempt))!.OrderNumber == "CUSTOM-13");
    await Reject<InvalidOperationException>(async () => await f.Buy(p, attempt: attempt));
    db.Orders.Add(new Order { Id = o.Id, OrderNumber = "CUSTOM-13", Customer = f.Customer() }); await Reject<DbUpdateException>(() => db.SaveChangesAsync());
});
Test("14 orders and snapshots merge by GUID with sandbox isolation", async f =>
{
    var p = await f.Product(4); var a = await f.Buy(p); var b = await f.Buy(p); await using var db = f.Db(); var service = f.Orders(db);
    Check((await service.GetCustomerOrdersByPhoneAsync("0501234567", false)).Orders.Count == 2);
    await service.UpdateStatusAsync(a.Id, OrderStatus.Ready, f.Token); await service.DeleteOrderAsync(a.Id);
    var merged = (await service.GetCustomerOrdersByPhoneAsync("0501234567", false)).Orders;
    Check(merged.Count == 2 && merged.Any(x => x.OrderId == a.Id && x.IsHistorical) && merged.Any(x => x.OrderId == b.Id && !x.IsHistorical));
    await db.Products.Where(x => x.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPublished, false)); var sandbox = await f.Buy(p, sandbox: true);
    Check(sandbox.IsSandbox && (await service.GetCustomerOrdersByPhoneAsync("0501234567", false)).Orders.Count == 2);
    Check((await service.GetCustomerOrdersByPhoneAsync("0501234567", true, f.Token)).Orders.Count == 3);
});
Test("15 edit normalizes, validates, no-op; current historical sandbox numbers blocked", async f =>
{
    var p = await f.Product(6); var a = await f.Buy(p); var b = await f.Buy(p); await using var db = f.Db(); var service = f.Orders(db);
    Check(await service.EditOrderNumberAsync(a.Id, " new-15 ", 0, "Test reason", f.Token));
    Check(!await service.EditOrderNumberAsync(a.Id, "NEW-15", 1, "Test", f.Token) && await db.OrderNumberAudits.CountAsync() == 1);
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(a.Id, b.OrderNumber, 1, "Test", f.Token));
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(a.Id, "bad space", 1, "Test", f.Token));
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(a.Id, "OK", 1, " ", f.Token));
    await db.Products.Where(x => x.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPublished, false)); var sandbox = await f.Buy(p, sandbox: true);
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(a.Id, sandbox.OrderNumber, 1, "Test", f.Token));
    await service.UpdateStatusAsync(b.Id, OrderStatus.Ready, f.Token); await service.DeleteOrderAsync(b.Id);
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(a.Id, b.OrderNumber, 1, "Test", f.Token));
});
Test("16 concurrent same number and stale editors; edit alongside checkout", async f =>
{
    var p = await f.Product(8); var a = await f.Buy(p); var b = await f.Buy(p);
    async Task Edit(Guid id, string n, int revision = 0) { await using var db = f.Db(); await f.Orders(db).EditOrderNumberAsync(id, n, revision, "Test", f.Token); }
    Check(await Race(() => Edit(a.Id, "RACE-16"), () => Edit(b.Id, "RACE-16")) == 1); var c = await f.Buy(p);
    Check(await Race(() => Edit(c.Id, "EDIT-16-A"), () => Edit(c.Id, "EDIT-16-B")) == 1);
    Check(await Race(() => Edit(c.Id, "CHECKOUT-16", 1), async () => await f.Buy(p)) == 2);
    await using var check = f.Db(); Check((await check.Orders.Select(x => x.OrderNumber).ToListAsync()).Distinct().Count() == 4);
});
Test("17 unauthorized rejected; audit failure rolls back all changes", async f =>
{
    var p = await f.Product(2); var o = await f.Buy(p); await using var db = f.Db(); var service = f.Orders(db);
    await Reject<UnauthorizedAccessException>(() => service.EditOrderNumberAsync(o.Id, "AUTH-17", 0, "Test", "invalid"));
    await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailAudit BEFORE INSERT ON OrderNumberAudits BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
    await Reject<InvalidOperationException>(() => service.EditOrderNumberAsync(o.Id, "FAIL-17", 0, "Test", f.Token));
    Check((await service.GetOrderAsync(o.Id))!.OrderNumber == o.OrderNumber && await db.OrderNumberAudits.CountAsync() == 0
        && !await db.OrderNumberReservations.AnyAsync(x => x.Number == "FAIL-17") && (await db.SalesHistory.SingleAsync()).OrderNumber == o.OrderNumber);
});
Test("18 edit preserves identity relationships amounts timestamps stock history reports", async f =>
{
    var p = await f.Product(5); var o = await f.Buy(p, q: 2); await using var db = f.Db(); var service = f.Orders(db);
    var before = await service.GetOrderAsync(o.Id); var hBefore = await db.SalesHistory.AsNoTracking().SingleAsync();
    Check(await service.EditOrderNumberAsync(o.Id, "RENUMBER-18", 0, "Correction", f.Token));
    var after = await service.GetOrderAsync(o.Id); var hAfter = await db.SalesHistory.AsNoTracking().SingleAsync();
    Check(after!.Id == before!.Id && after.Customer.Id == before.Customer.Id && after.CreatedAt == before.CreatedAt && after.UpdatedAt == before.UpdatedAt
        && after.Status == before.Status && after.FinalTotal == before.FinalTotal && JsonSerializer.Serialize(after.Items) == JsonSerializer.Serialize(before.Items) && await f.Quantity(p) == 3);
    hBefore.OrderNumber = hAfter.OrderNumber; Check(JsonSerializer.Serialize(hBefore) == JsonSerializer.Serialize(hAfter));
    var audit = await db.OrderNumberAudits.SingleAsync(); Check(audit.PreviousNumber == o.OrderNumber && audit.AdminIdentity == "test-admin" && audit.Reason == "Correction");
    Check((await new ReportsService(db, f.Auth(db)).GetSalesHistoryAsync(new("RENUMBER-18", null, null, null, 1, 10))).TotalCount == 1);
    Check((await service.GetCustomerOrdersByPhoneAsync("0501234567", false)).Orders.Single().OrderNumber == "RENUMBER-18");
    await using var restarted = f.Db(); Check((await f.Orders(restarted).GetOrderAsync(o.Id))!.OrderNumber == "RENUMBER-18");
});
Test("inventory admin edit versus checkout detects stale snapshot without lost sizes", async f =>
{
    var p = await f.Product(2, 3, 4); await using var db = f.Db(); var products = f.Products(db);
    var edit = (await products.GetAllProductsAsync()).Single(); edit.InventorySnapshot = edit.Sizes.ToDictionary(x => x.Id, x => x.Quantity); edit.Sizes.Single(x => x.SizeName == "M").Quantity = 7;
    Check(await Race(() => products.UpdateProductWithRelationsAsync(edit, [], []), async () => await f.Buy(p)) >= 1);
    var quantities = await f.Quantities(p); Check(quantities[0] == 1 && quantities[2] == 4 && quantities[1] is 3 or 7);
    await Reject<InvalidOperationException>(() => products.UpdateProductWithRelationsAsync(edit, [], []));
    var fresh = (await products.GetAllProductsAsync()).Single(); fresh.InventorySnapshot = fresh.Sizes.ToDictionary(x => x.Id, x => x.Quantity);
    fresh.Sizes.Single(x => x.SizeName == "M").Quantity = 8; await products.UpdateProductWithRelationsAsync(fresh, [], []);
    Check((await f.Quantities(p))[1] == 8 && (await products.GetAllProductsAsync()).Single().Sizes.Select(x => x.Id).ToHashSet().SetEquals(p.Sizes.Select(x => x.Id)));
});
Test("SQLite quantity concurrency token rejects stale direct EF writes", async f =>
{
    var p = await f.Product(1); await using var stale = f.Db(); var size = await stale.ProductSizes.SingleAsync(); await f.Buy(p); size.Quantity = 5;
    await Reject<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync()); Check(await f.Quantity(p) == 0);
});
Test("SQLite guards reject bypass collisions and reservation deletion", async f =>
{
    var p = await f.Product(3); var a = await f.Buy(p); var b = await f.Buy(p); await using var db = f.Db();
    await Reject<SqliteException>(() => db.Orders.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OrderNumber, a.OrderNumber)));
    await Reject<SqliteException>(() => db.OrderNumberReservations.ExecuteDeleteAsync());
    db.OrderNumberReservations.Add(new() { Number = a.OrderNumber.ToLowerInvariant(), OrderId = b.Id }); await Reject<DbUpdateException>(() => db.SaveChangesAsync());
});
Test("multiple categories images and fixed sale price preserved during stock edit", async f =>
{
    var p = await f.Product(2, 3); await using var db = f.Db();
    var categories = new[] { new Category { Id = Guid.NewGuid(), Name = "One" }, new Category { Id = Guid.NewGuid(), Name = "Two" } };
    var discount = new Discount { Id = Guid.NewGuid(), Name = "Fixed", RuleType = DiscountRuleType.FixedSalePrice, Value = 7 };
    db.Categories.AddRange(categories); db.Discounts.Add(discount);
    db.ProductDiscounts.Add(new() { ProductId = p.Id, DiscountId = discount.Id });
    db.ProductCategories.AddRange(categories.Select(c => new ProductCategory { ProductId = p.Id, CategoryId = c.Id })); await db.SaveChangesAsync();
    var products = f.Products(db); var edit = (await products.GetAllProductsAsync()).Single();
    Check(edit.StorePrice == 7); edit.InventorySnapshot = edit.Sizes.ToDictionary(x => x.Id, x => x.Quantity);
    edit.Sizes.Single(x => x.SizeName == "M").Quantity = 5;
    var urls = edit.Images.OrderBy(x => x.SortOrder).Select(x => x.ImageUrl).ToArray();
    await products.UpdateProductWithRelationsAsync(edit, [discount.Id], categories.Select(x => x.Id).ToList());
    var fresh = (await products.GetAllProductsAsync()).Single();
    Check(fresh.StorePrice == 7 && fresh.IsPublished && fresh.ProductCategories.Count == 2
        && fresh.Images.OrderBy(x => x.SortOrder).Select(x => x.ImageUrl).SequenceEqual(urls));
    var valid = await products.BuildValidCartItemsAsync([new PersistedCartItem { ProductId = p.Id, Size = "S", Quantity = 1 }]);
    var cart = new CartService(); cart.ReplaceItems(valid);
    var order = await f.Orders(db).CreateOrderAsync(f.Customer(), cart.Items.ToList(), cart.CalculateBestDiscount());
    Check(order.FinalTotal == 7 && await f.Quantity(p) == 1 && (await f.Quantities(p))[1] == 5);
});
tests.Add(("migration preserves prior official sandbox history products images and restart", async () =>
{
    var f = await Fixture.Create(previous: true); var p = await f.Product(2, 3, 4); await f.SeedLegacy("OLD-OFFICIAL", false); await f.SeedLegacy("OLD-SANDBOX", true);
    await using var db = f.Db(); await db.Database.MigrateAsync(); var orders = await db.Orders.Include(x => x.Items).ToListAsync();
    Check(orders.Count == 2 && orders.Select(x => x.OrderNumber).ToHashSet().SetEquals(new[] { "OLD-OFFICIAL", "OLD-SANDBOX" }) && orders.All(x => x.NumberRevision == 0 && x.FinalTotal == 15 && x.Items.Count == 1));
    Check(await db.SalesHistory.CountAsync() == 1 && await db.OrderNumberReservations.CountAsync() == 2 && await db.ProductImages.CountAsync() == 2 && (await f.Quantities(p)).SequenceEqual(new[] { 2, 3, 4 }));
    await db.Database.MigrateAsync(); Check(await db.OrderNumberReservations.CountAsync() == 2 && await db.OrderNumberAudits.CountAsync() == 0);
    var c = db.Database.GetDbConnection(); await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA foreign_key_check"; using var reader = await cmd.ExecuteReaderAsync(); Check(!await reader.ReadAsync());
}));
tests.Add(("migration ambiguous legacy duplicate refuses atomically with diagnostic dry-run", async () =>
{
    var f = await Fixture.Create(previous: true); await f.SeedLegacy("DUPLICATE", false); await f.SeedLegacy("DUPLICATE", true); await using var db = f.Db();
    await Reject<SqliteException>(() => db.Database.MigrateAsync()); Check(!(await db.Database.GetAppliedMigrationsAsync()).Any(x => x.EndsWith("AddSafeOrderNumbers")));
    Check(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Orders WHERE OrderNumber = 'DUPLICATE'").SingleAsync() == 2);
    Check(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Orders') WHERE name = 'NumberRevision'").SingleAsync() == 0);
    Check(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'OrderNumberReservations'").SingleAsync() == 0);
    var beforeHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(f.Paths.DatabasePath));
    Check(await Diagnostics.Run(f.Paths.DatabasePath) == 0);
    Check(beforeHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(f.Paths.DatabasePath))));
}));
tests.Add(("migration rejects mismatched linked history and malformed numbers", async () =>
{
    foreach (var malformed in new[] { false, true })
    {
        var f = await Fixture.Create(previous: true); await f.SeedLegacy(malformed ? "INVALID SPACE" : "VALID", false); await using var db = f.Db();
        if (!malformed) await db.SalesHistory.ExecuteUpdateAsync(s => s.SetProperty(x => x.OrderNumber, "OTHER"));
        await Reject<SqliteException>(() => db.Database.MigrateAsync());
        Check(!(await db.Database.GetAppliedMigrationsAsync()).Any(x => x.EndsWith("AddSafeOrderNumbers")));
    }
}));
tests.Add(("migration Down preserves prior data on isolated fixture", async () =>
{
    var f = await Fixture.Create(); var p = await f.Product(2); await f.Buy(p); await using var db = f.Db();
    await db.GetService<IMigrator>().MigrateAsync("20260825203549_AddStoreChatSettings");
    Check(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Orders").SingleAsync() == 1 && await f.Quantity(p) == 1);
    Check(await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM SalesHistory").SingleAsync() == 1);
}));
tests.Add(("migration never infers unlinked history ownership from equal GUID", async () =>
{
    var f = await Fixture.Create(previous: true); await f.SeedLegacy("AMBIGUOUS", false); await using var db = f.Db();
    var id = await db.Database.SqlQueryRaw<string>("SELECT Id AS Value FROM Orders").SingleAsync();
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SalesHistory SET Id = {id}, OriginalOrderId = NULL");
    await Reject<SqliteException>(() => db.Database.MigrateAsync());
    Check(!(await db.Database.GetAppliedMigrationsAsync()).Any(x => x.EndsWith("AddSafeOrderNumbers")));
}));
SizeIdentityTests.Register(tests);
HistoricalInventoryReviewTests.Register(tests);
DeploymentSafetyTests.Register(tests);
var failures = 0;
foreach (var (name, run) in tests)
{ try { await run(); Console.WriteLine($"PASS {name}"); } catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); } }
Console.WriteLine($"RESULT: {tests.Count - failures}/{tests.Count} passed; isolated fixtures retained in {Fixture.TestRoot}");
return failures == 0 ? 0 : 1;
