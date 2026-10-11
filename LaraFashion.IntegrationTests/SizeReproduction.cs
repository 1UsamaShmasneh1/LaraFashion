using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

static class SizeReproduction
{
    // Run against the pre-fix checkout; retained as incident reproduction evidence.
    public static async Task<int> Run()
    {
        foreach (var duplicateQuantity in new int?[] { null, 3, 0 })
        {
            var f = await Fixture.Create(); await using var db = f.Db();
            var p = new Product { Name = "كرومي", OriginalPrice = 35, Sizes = new[] { 2, 4, 3, 4 }
                .Select((q, i) => new ProductSize { SizeName = $"{i + 1} سنه", Quantity = q }).ToList() };
            if (duplicateQuantity.HasValue) p.Sizes.Add(new() { SizeName = "3 سنه", Quantity = duplicateQuantity.Value });
            try
            {
                await f.Products(db).AddProductWithRelationsAsync(p, [], []);
                await f.Products(db).PublishProductAsync(p.Id);
            }
            catch (InvalidOperationException ex) { Console.WriteLine($"SAVE blocked duplicate={duplicateQuantity}: {ex.Message}"); continue; }
            var visible = await f.Products(db).GetProductAsync(p.Id);
            Console.WriteLine($"REPRO duplicate={duplicateQuantity?.ToString() ?? "none"}; displayed first 3 سنه stock={visible!.Sizes.First(x => x.SizeName == "3 سنه").Quantity}");
            try { await f.Buy(p, "3 سنه"); Console.WriteLine("CHECKOUT accepted"); }
            catch (InvalidOperationException ex) { Console.WriteLine($"CHECKOUT rejected: {ex.Message}"); }
            var quantities = await db.ProductSizes.AsNoTracking().Where(x => x.ProductId == p.Id)
                .OrderBy(x => x.SizeName).Select(x => new { x.SizeName, x.Quantity }).ToListAsync();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(quantities));
        }
        return 0;
    }
}
