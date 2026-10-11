using LaraFashion.Models;
using LaraFashion.Models.Enums;
using LaraFashion.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class SizeIdentityTests
{
    static void Check(bool condition, string message = "Size identity assertion failed") { if (!condition) throw new Exception(message); }
    static async Task Reject(Func<Task> action, InventoryFailureReason? reason = null)
    {
        try { await action(); }
        catch (InvalidOperationException ex) { if (reason.HasValue) Check(ex is InventoryException i && i.Reason == reason, ex.Message); return; }
        throw new Exception("Expected rejection");
    }
    static async Task SqlReject(Func<Task> action)
    { try { await action(); } catch (SqliteException) { return; } catch (DbUpdateException) { return; } throw new Exception("Expected SQLite guard rejection"); }
    static Product Draft(params (string Label, int Quantity)[] rows) => new() { Name = "كرومي", OriginalPrice = 35,
        Sizes = rows.Select(r => new ProductSize { Id = Guid.NewGuid(), SizeName = r.Label, Quantity = r.Quantity }).ToList() };
    static Product Edit(Product p) => new() { Id = p.Id, Name = p.Name, OriginalPrice = p.OriginalPrice,
        IsActive = p.IsActive, InventorySnapshot = p.Sizes.ToDictionary(s => s.Id, s => s.Quantity),
        Sizes = p.Sizes.Select(s => new ProductSize { Id = s.Id, ProductId = s.ProductId, SizeName = s.SizeName, Quantity = s.Quantity }).ToList() };
    static CartItem Item(Product p, int index = 0, int quantity = 1) => new() { ProductId = p.Id, ProductSizeId = p.Sizes[index].Id,
        ProductName = p.Name, Size = p.Sizes[index].SizeName, Quantity = quantity, UnitPrice = p.StorePrice };
    static PersistedCartItem Persist(CartItem i) => new() { ProductId = i.ProductId, ProductSizeId = i.ProductSizeId, Size = i.Size, Quantity = i.Quantity };
    static async Task<Product> Create(Fixture f, params (string, int)[] rows)
    {
        var p = Draft(rows); await using var db = f.Db();
        await f.Products(db).AddProductWithRelationsAsync(p, [], []); await f.Products(db).PublishProductAsync(p.Id);
        var fresh = (await f.Products(db).GetProductAsync(p.Id))!;
        fresh.Sizes = fresh.Sizes.OrderBy(s => Array.FindIndex(rows, r => r.Item1 == s.SizeName)).ToList();
        return fresh;
    }
    static async Task Buy(Fixture f, params CartItem[] items)
    { await using var db = f.Db(); await f.Orders(db).CreateOrderAsync(f.Customer(), items.ToList(), new() { OriginalTotal = items.Sum(i => i.TotalPrice) }); }
    static async Task CorruptDuplicate(Fixture f, Product p, int quantity)
    {
        await using var db = f.Db();
        // Only this disposable fixture simulates legacy corruption; production guards stay intact.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_ProductSizes_ProductId_NormalizedName");
        db.ProductSizes.Add(new() { Id = Guid.NewGuid(), ProductId = p.Id, SizeName = p.Sizes[0].SizeName, Quantity = quantity }); await db.SaveChangesAsync();
    }
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Func<Fixture, Task> run) => tests.Add(("size " + name, async () => await run(await Fixture.Create())));
        Test("01 reference add publish restore checkout 3->2 others 2/4/4; exhaustion", async f =>
        {
            var p = await Create(f, ("1 سنه", 2), ("2 سنه", 4), ("3 سنه", 3), ("4 سنه", 4)); await using var db = f.Db();
            var cart = new CartService(); cart.AddToCart(p, p.Sizes.Single(s => s.SizeName == "3 سنه").Id, 1);
            var json = JsonSerializer.Serialize(cart.ToPersistedItems());
            cart.ReplaceItems(await f.Products(db).BuildValidCartItemsAsync(JsonSerializer.Deserialize<List<PersistedCartItem>>(json)!));
            var order = await f.Orders(db).CreateOrderAsync(f.Customer(), cart.Items, cart.CalculateBestDiscount());
            Check(order.Items.Single().ProductSizeId == cart.Items.Single().ProductSizeId && order.FinalTotal == 35);
            foreach (var (label, expected) in new[] { ("1 سنه", 2), ("2 سنه", 4), ("3 سنه", 2), ("4 سنه", 4) }) Check(await f.Quantity(p, label) == expected);
            await Buy(f, new CartItem { ProductId = p.Id, ProductSizeId = cart.Items[0].ProductSizeId, Size = "3 سنه", Quantity = 2, UnitPrice = 35 });
            var fresh = (await f.Products(db).GetActiveProductsAsync()).Single();
            Check(fresh.IsPublished && !SizeNamePolicy.IsSelectable(fresh, fresh.Sizes.Single(s => s.SizeName == "3 سنه")) && fresh.Sizes.Count == 4);
        });
        Test("02 add/edit validation duplicates invalid quantities owner/id; preserve input", async f =>
        {
            await using var db = f.Db(); var service = f.Products(db);
            foreach (var rows in new[] { new[] { ("3 سنه", 2), (" 3\u00a0سنه\u200b ", 0) }, new[] { ("", 2) }, new[] { ("S", -1) } })
            { var p = Draft(rows); var original = JsonSerializer.Serialize(p.Sizes); await Reject(() => service.AddProductAsync(p)); Check(p.Id == Guid.Empty && original == JsonSerializer.Serialize(p.Sizes)); }
            var a = await Create(f, ("S", 2), ("M", 4)); var b = await Create(f, ("S", 2));
            var edit = Edit(a); edit.Sizes[1].SizeName = " s "; await Reject(() => service.UpdateProductAsync(edit));
            edit = Edit(a); edit.Sizes[1].Id = edit.Sizes[0].Id; await Reject(() => service.UpdateProductAsync(edit));
            edit = Edit(a); edit.Sizes[1] = b.Sizes[0]; await Reject(() => service.UpdateProductAsync(edit));
            var invalidOwner = Draft(("L", 1)); invalidOwner.Sizes[0].ProductId = a.Id; await Reject(() => service.AddProductAsync(invalidOwner));
            Check(await f.Quantity(a, "S") == 2 && await db.Products.CountAsync() == 2);
        });
        Test("03 single/bulk publish validates all; no partial publish; zero permitted", async f =>
        {
            var p = await Create(f, ("S", 2)); await CorruptDuplicate(f, p, 0); await using var db = f.Db();
            await db.Products.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsPublished, false));
            var zero = Draft(("L", 0)); await f.Products(db).AddProductAsync(zero);
            await Reject(() => f.Products(db).PublishProductAsync(p.Id)); await Reject(() => f.Products(db).PublishAllUnpublishedProductsAsync());
            Check(await db.Products.CountAsync(p => p.IsPublished) == 0);
            await f.Products(db).PublishProductAsync(zero.Id); Check((await f.Products(db).GetProductAsync(zero.Id))!.IsPublished);
        });
        Test("04 normalization SQLite parity whitespace ASCII case invisibles distinct semantics", async f =>
        {
            await using var db = f.Db();
            var labels = new[] { " 3\u00a0سنه\u200b ", "3 سنه", " s ", "S", "3 سنوات", "3-4", "34", "é", "É", "😀", "\u200b", "\0", new string(' ', 128) + "S" }
                .Concat(SizeNamePolicy.Spaces.Concat(SizeNamePolicy.Invisible).Select(c => $" a{c}b ")).ToList();
            var connection = db.Database.GetDbConnection(); await connection.OpenAsync();
            foreach (var label in labels)
            {
                using var command = connection.CreateCommand(); command.CommandText = $"SELECT {SizeNamePolicy.SqlKey("$label")}, CASE WHEN {SizeNamePolicy.SqlValid("$label")} THEN 1 ELSE 0 END";
                var parameter = command.CreateParameter(); parameter.ParameterName = "$label"; parameter.Value = label; command.Parameters.Add(parameter);
                using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
                Check(reader.GetString(0) == SizeNamePolicy.Key(label), "SQL/C# key mismatch"); Check((reader.GetInt32(1) == 1) == SizeNamePolicy.IsValid(label), "SQL/C# validation mismatch");
            }
            Check(SizeNamePolicy.Key("3 سنه") != SizeNamePolicy.Key("3 سنوات") && SizeNamePolicy.Key("3-4") != SizeNamePolicy.Key("34"));
            await Create(f, ("3 سنه", 1), ("3 سنوات", 1), ("3-4", 1), ("34", 1));
        });
        Test("05 legacy duplicate positive+positive and zero+positive reject checkout/cancel atomically", async f =>
        {
            foreach (var duplicate in new[] { 3, 0 })
            {
                var isolated = await Fixture.Create(); var p = await Create(isolated, ("3 سنه", 3), ("4 سنه", 4));
                await using var db = isolated.Db(); var order = await isolated.Orders(db).CreateOrderAsync(isolated.Customer(), [Item(p)], new() { OriginalTotal = 35 });
                await CorruptDuplicate(isolated, p, duplicate);
                var before = await db.ProductSizes.AsNoTracking().OrderBy(s => s.Id).Select(s => s.Quantity).ToArrayAsync();
                var legacy = Item(p); legacy.ProductSizeId = null;
                await Reject(() => Buy(isolated, legacy), InventoryFailureReason.AmbiguousSize);
                await Reject(() => Buy(isolated, Item(p)), InventoryFailureReason.AmbiguousSize);
                await Reject(() => isolated.Orders(db).UpdateStatusAsync(order.Id, OrderStatus.Cancelled, f.Token), InventoryFailureReason.AmbiguousSize);
                Check(before.SequenceEqual(await db.ProductSizes.AsNoTracking().OrderBy(s => s.Id).Select(s => s.Quantity).ToArrayAsync()));
                var visible = (await isolated.Products(db).GetActiveProductsAsync()).Single();
                Check(!SizeNamePolicy.IsSelectable(visible, visible.Sizes.First(s => s.SizeName == "3 سنه")) && SizeNamePolicy.IsSelectable(visible, visible.Sizes.Single(s => s.SizeName == "4 سنه")) && SizeNamePolicy.Warning(visible) is not null);
            }
        });
        Test("06 old cart unique upgrades ID; ambiguous/missing stays review; quantities retained", async f =>
        {
            var p = await Create(f, ("S", 3), ("M", 4)); await using var db = f.Db();
            var old = new PersistedCartItem { ProductId = p.Id, Size = " s ", Quantity = 2 };
            var restored = await f.Products(db).BuildValidCartItemsAsync([old]);
            Check(restored.Single().ProductSizeId == p.Sizes.Single(s => s.SizeName == "S").Id && restored.Single().Quantity == 2 && restored.Single().ReviewMessage is null);
            await CorruptDuplicate(f, p, 0);
            restored = await f.Products(db).BuildValidCartItemsAsync([old, new() { ProductId = p.Id, Size = "M", Quantity = 8 }, new() { ProductId = Guid.NewGuid(), Size = "M", Quantity = 1 }]);
            Check(restored.Count == 3 && restored.All(i => i.ReviewMessage is not null) && restored[1].Quantity == 8 && restored[0].ProductSizeId is null);
        });
        Test("07 rename same ID requires review and prevents checkout/cancellation", async f =>
        {
            var p = await Create(f, ("3 سنه", 3)); var selected = Item(p); await using var db = f.Db();
            var order = await f.Orders(db).CreateOrderAsync(f.Customer(), [selected], new() { OriginalTotal = 35 });
            var fresh = (await f.Products(db).GetProductAsync(p.Id))!; var edit = Edit(fresh); edit.Sizes[0].SizeName = "4 سنه";
            await f.Products(db).UpdateProductAsync(edit);
            await Reject(() => Buy(f, selected), InventoryFailureReason.SizeChanged);
            Check((await f.Products(db).BuildValidCartItemsAsync([Persist(selected)])).Single().ReviewMessage is not null);
            await Reject(() => f.Orders(db).UpdateStatusAsync(order.Id, OrderStatus.Cancelled, f.Token), InventoryFailureReason.SizeChanged);
            Check(await f.Quantity(p, "4 سنه") == 2);
        });
        Test("08 delete/recreate size and product never rebind stable cart; ledger prevents reused ID", async f =>
        {
            var p = await Create(f, ("3 سنه", 3)); var selected = Item(p); await using var db = f.Db();
            var edit = Edit(p); edit.Sizes.Clear(); await f.Products(db).UpdateProductAsync(edit);
            edit = Edit((await f.Products(db).GetProductAsync(p.Id))!); edit.Sizes.Add(new() { Id = Guid.NewGuid(), SizeName = "3 سنه", Quantity = 3 });
            await f.Products(db).UpdateProductAsync(edit); await Reject(() => Buy(f, selected), InventoryFailureReason.StaleIdentity);
            var oldRow = p.Sizes[0]; db.ProductSizes.Add(oldRow); await SqlReject(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
            await f.Products(db).DeleteProductAsync(p.Id); var recreated = await Create(f, ("3 سنه", 3));
            await Reject(() => Buy(f, selected), InventoryFailureReason.ProductUnavailable);
            Check((await f.Products(db).BuildValidCartItemsAsync([Persist(selected)])).Single().ReviewMessage is not null && await f.Quantity(recreated, "3 سنه") == 3);
        });
        Test("09 reorder remove middle append keeps original IDs; repeated save no duplicate", async f =>
        {
            var p = await Create(f, ("1 سنه", 2), ("2 سنه", 4), ("3 سنه", 3)); await using var db = f.Db();
            var edit = Edit(p); edit.Sizes.Reverse(); var removed = edit.Sizes[1].Id; edit.Sizes.RemoveAt(1);
            var added = new ProductSize { Id = Guid.NewGuid(), SizeName = "4 سنه", Quantity = 4 }; edit.Sizes.Add(added);
            await f.Products(db).UpdateProductAsync(edit);
            var fresh = (await f.Products(db).GetProductAsync(p.Id))!;
            Check(fresh.Sizes.Count == 3 && fresh.Sizes.All(s => s.Id != removed) && fresh.Sizes.Single(s => s.SizeName == "3 سنه").Id == p.Sizes.Single(s => s.SizeName == "3 سنه").Id && fresh.Sizes.Any(s => s.Id == added.Id));
            await f.Products(db).UpdateProductAsync(Edit(fresh)); Check(await db.ProductSizes.CountAsync() == 3);
        });
        Test("10 failed graph add retry fresh context; duplicate save guard; stale edit cannot reset stock", async f =>
        {
            var draft = Draft(("S", 3), ("M", 4)); await using var db = f.Db(); var service = f.Products(db);
            await Reject(() => service.AddProductWithRelationsAsync(draft, [Guid.NewGuid()], []));
            Check(draft.Id == Guid.Empty && await db.Products.CountAsync() == 0 && await db.ProductSizes.CountAsync() == 0 && !db.ChangeTracker.Entries().Any());
            await service.AddProductWithRelationsAsync(draft, [], []); await Reject(() => service.AddProductAsync(draft)); await service.PublishProductAsync(draft.Id);
            var edit = Edit((await service.GetProductAsync(draft.Id))!); await f.Buy(draft);
            await Reject(() => service.UpdateProductAsync(edit)); Check(await f.Quantity(draft) == 2 && await db.Products.CountAsync() == 1);
            var parallelDraft = Draft(("L", 0)); parallelDraft.Sizes[0].Id = Guid.Empty;
            async Task<bool> TryAdd() { try { await service.AddProductAsync(parallelDraft); return true; } catch (InvalidOperationException) { return false; } }
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrivals = 0;
            var parallel = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
                await start.Task; return await TryAdd();
            })).ToArray();
            await ready.Task; start.SetResult();
            Check((await Task.WhenAll(parallel)).Count(x => x) == 1 && await db.Products.CountAsync() == 2);
        });
        Test("11 wrong owner ID, grouped overflow and later line failures roll back", async f =>
        {
            var p = await Create(f, ("S", 3), ("M", 4)); var other = await Create(f, ("S", 3));
            var wrong = Item(p); wrong.ProductSizeId = other.Sizes[0].Id; await Reject(() => Buy(f, wrong), InventoryFailureReason.StaleIdentity);
            await Reject(() => Buy(f, Item(p, 0, int.MaxValue), Item(p, 0, 1)), InventoryFailureReason.InvalidQuantity);
            await Reject(() => Buy(f, Item(p), Item(p, 1, 5)), InventoryFailureReason.InsufficientStock);
            Check(await f.Quantity(p) == 3 && await f.Quantity(p, "M") == 4);
            await Buy(f, Item(p), Item(p)); Check(await f.Quantity(p) == 1);
        });
        Test("12 old completed attempt accepts new identity contract and cannot create another order", async f =>
        {
            var p = await Create(f, ("S", 3)); await using var db = f.Db(); var service = f.Orders(db);
            var customer = f.Customer(); var item = Item(p); var attempt = Guid.NewGuid(); var total = new CartDiscountResult { OriginalTotal = 35 };
            var order = await service.CreateOrderAsync(customer, [item], total, false, attempt);
            var legacyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { customer.FullName, customer.PhoneNumber, customer.City, customer.Street, customer.HouseNumber,
                Items = new[] { new { item.ProductId, item.Size, item.Quantity, item.UnitPrice } }, total.OriginalTotal, total.DiscountAmount, total.DiscountName, allowUnpublished = false }))));
            await db.CheckoutAttempts.Where(a => a.Id == attempt).ExecuteUpdateAsync(s => s.SetProperty(a => a.RequestHash, legacyHash));
            Check((await service.CreateOrderAsync(customer, [item], total, false, attempt)).Id == order.Id && await f.Quantity(p) == 2 && await db.Orders.CountAsync() == 1);
            item.Quantity = 2; await Reject(() => service.CreateOrderAsync(customer, [item], total, false, attempt));
            var receipt = await service.GetCheckoutReceiptAsync(attempt); Check(receipt!.OrderId == order.Id);
        });
        Test("13 cancellation/reactivation/bulk exact identity once; historical null blocks without guessing", async f =>
        {
            var p = await Create(f, ("S", 3), ("M", 4)); await using var db = f.Db(); var service = f.Orders(db);
            var a = await service.CreateOrderAsync(f.Customer(), [Item(p)], new() { OriginalTotal = 35 });
            var b = await service.CreateOrderAsync(f.Customer(), [Item(p, 1)], new() { OriginalTotal = 35 });
            await service.UpdateStatusesAsync([a.Id, b.Id], OrderStatus.Cancelled, f.Token); await service.UpdateStatusesAsync([a.Id, b.Id], OrderStatus.Cancelled, f.Token);
            Check(await f.Quantity(p) == 3 && await f.Quantity(p, "M") == 4);
            await service.UpdateStatusesAsync([a.Id, b.Id], OrderStatus.New, f.Token); Check(await f.Quantity(p) == 2 && await f.Quantity(p, "M") == 3);
            await db.OrderItems.Where(i => i.OrderId == b.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProductSizeId, (Guid?)null));
            await Reject(() => service.UpdateStatusesAsync([a.Id, b.Id], OrderStatus.Cancelled, f.Token), InventoryFailureReason.HistoricalIdentityUnknown);
            Check(await f.Quantity(p) == 2 && await f.Quantity(p, "M") == 3 && await db.Orders.CountAsync(o => o.Status == OrderStatus.New) == 2);
        });
        Test("14 migration previous data/triggers audit reservations; null history retained; Down/Up", async f =>
        {
            var legacy = await Fixture.Create(previous: true); var p = await legacy.Product(2, 3, 4); await legacy.SeedLegacy("SIZE-LEGACY", false); await using var db = legacy.Db();
            await db.GetService<IMigrator>().MigrateAsync("20261003154113_AddSafeOrderNumbers");
            // Seed a legitimate audit using the old schema, without running the new model between migrations.
            var orderId = await db.Database.SqlQueryRaw<Guid>("SELECT Id AS Value FROM Orders").SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderNumberReservations (Number, OrderId) VALUES ({"SIZE-EDIT"}, {orderId})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OrderNumberAudits (Id, OrderId, PreviousNumber, NewNumber, AdminIdentity, ChangedAtUtc, Reason) VALUES ({Guid.NewGuid()}, {orderId}, {"SIZE-LEGACY"}, {"SIZE-EDIT"}, {"test-admin"}, {DateTime.UtcNow}, {"Test migration"})");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Orders SET OrderNumber={"SIZE-EDIT"}, NumberRevision=1 WHERE Id={orderId}");
            var triggerCount = await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger'").SingleAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261005162501_AddStableProductSizeIdentity");
            Check((await legacy.Quantities(p)).SequenceEqual(new[] { 2, 3, 4 }) && await db.ProductImages.CountAsync() == 2 && await db.OrderNumberAudits.CountAsync() == 1 && await db.OrderNumberReservations.CountAsync() == 2);
            Check(await db.OrderItems.AllAsync(i => i.ProductSizeId == null) && await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger'").SingleAsync() == triggerCount + 7);
            await db.Database.MigrateAsync();
            await SqlReject(() => db.Database.ExecuteSqlRawAsync("DELETE FROM OrderNumberReservations"));
            await Reject(() => legacy.Orders(db).UpdateStatusAsync(orderId, OrderStatus.Cancelled, legacy.Token), InventoryFailureReason.HistoricalIdentityUnknown);
            await db.GetService<IMigrator>().MigrateAsync("20261003154113_AddSafeOrderNumbers");
            Check(await db.OrderNumberAudits.CountAsync() == 1 && await db.OrderNumberReservations.CountAsync() == 2);
            await db.Database.MigrateAsync(); Check(await db.OrderItems.AllAsync(i => i.ProductSizeId == null));
        });
        Test("15 migration conflicts stop before schema/data change; read-only diagnostic hash unchanged", async f =>
        {
            foreach (var invalid in new[] { "  s\u00a0", "\u200b", "negative", "bad-id" })
            {
                var legacy = await Fixture.Create(previous: true); var p = await legacy.Product(3); await using var db = legacy.Db();
                await db.GetService<IMigrator>().MigrateAsync("20261003154113_AddSafeOrderNumbers");
                if (invalid == "negative") await db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET Quantity=-1");
                else if (invalid == "bad-id") await db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET Id='unmappable-id'");
                else await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProductSizes (Id, ProductId, SizeName, Quantity) VALUES ({Guid.NewGuid()}, {p.Id}, {invalid}, {0})");
                await SqlReject(() => db.Database.MigrateAsync());
                Check(!((await db.Database.GetAppliedMigrationsAsync()).Any(m => m.EndsWith("AddStableProductSizeIdentity"))) && await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_table_info('ProductSizes') WHERE name='NormalizedName'").SingleAsync() == 0);
                var before = SHA256.HashData(await File.ReadAllBytesAsync(legacy.Paths.DatabasePath));
                await Diagnostics.Run(legacy.Paths.DatabasePath); Check(before.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(legacy.Paths.DatabasePath))));
            }
        });
        Test("16 DB bypass duplicate wrong key negative overlarge; safe diagnostics no customer/recovery secrets", async f =>
        {
            var p = await Create(f, ("S", 3)); await using var db = f.Db();
            await SqlReject(() => db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET Quantity=-1"));
            await SqlReject(() => db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET Quantity=2147483648"));
            await SqlReject(() => db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET NormalizedName='WRONG'"));
            await SqlReject(() => db.Database.ExecuteSqlRawAsync("UPDATE ProductSizes SET SizeName='M'"));
            await SqlReject(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProductSizes (Id,ProductId,SizeName,NormalizedName,Quantity) VALUES ({"unmappable-id"},{p.Id},{"M"},{"M"},{1})"));
            await SqlReject(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProductSizes (Id,ProductId,SizeName,NormalizedName,Quantity) VALUES ({Guid.NewGuid()},{p.Id},{" s "},{"S"},{0})"));
            var logger = new CaptureLogger(); var service = new OrderService(f.Auth(db), f.Paths, f, logger);
            var customer = f.Customer(); customer.FullName = "SECRET-CUSTOMER-NAME"; customer.PhoneNumber = "SECRET-PHONE";
            var attempt = Guid.NewGuid(); await Reject(() => service.CreateOrderAsync(customer, [Item(p, 0, 4)], new(), false, attempt), InventoryFailureReason.InsufficientStock);
            var log = string.Join("\n", logger.Messages);
            Check(log.Contains("InsufficientStock") && log.Contains(p.Id.ToString()) && !log.Contains(customer.FullName) && !log.Contains(customer.PhoneNumber) && !log.Contains(attempt.ToString()));
        });
    }
    sealed class CaptureLogger : ILogger<OrderService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
