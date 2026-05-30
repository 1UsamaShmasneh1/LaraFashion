using LaraFashion.Data;
using LaraFashion.Models;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class ProductService
{
    private readonly AppDbContext _db;

    public ProductService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Product>> GetActiveProductsAsync()
    {
        return await _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Product>> GetAllProductsAsync()
    {
        return await _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<Product?> GetProductAsync(Guid id)
    {
        return await _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task AddProductAsync(Product product)
    {
        product.Id = Guid.NewGuid();
        product.CreatedAt = DateTime.Now;
        product.UpdatedAt = DateTime.Now;

        product.ProductDiscounts = new();

        foreach (var size in product.Sizes)
        {
            size.Id = Guid.NewGuid();
            size.ProductId = product.Id;
        }

        _db.Products.Add(product);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateProductAsync(Product product)
    {
        product.UpdatedAt = DateTime.Now;

        _db.Products.Update(product);

        await _db.SaveChangesAsync();
    }

    public async Task DeleteProductAsync(Guid id)
    {
        var product = await _db.Products
            .Include(x => x.Sizes)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (product is null)
            return;

        if (!string.IsNullOrWhiteSpace(product.ImageUrl) &&
            product.ImageUrl.StartsWith("/uploads/"))
        {
            var fileName = Path.GetFileName(product.ImageUrl);

            var filePath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "wwwroot",
                "uploads",
                fileName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        _db.ProductSizes.RemoveRange(product.Sizes);
        _db.Products.Remove(product);

        await _db.SaveChangesAsync();
    }

    public async Task UpdateProductWithSizesAsync(Product product)
    {
        var existingProduct = await _db.Products
            .Include(x => x.Sizes)
            .FirstOrDefaultAsync(x => x.Id == product.Id);

        if (existingProduct is null)
            return;

        existingProduct.Name = product.Name;
        existingProduct.SerialNumber = product.SerialNumber;
        existingProduct.Description = product.Description;
        existingProduct.OriginalPrice = product.OriginalPrice;
        existingProduct.DiscountType = product.DiscountType;
        existingProduct.DiscountValue = product.DiscountValue;
        existingProduct.ImageUrl = product.ImageUrl;
        existingProduct.IsActive = product.IsActive;
        existingProduct.UpdatedAt = DateTime.Now;

        _db.ProductSizes.RemoveRange(existingProduct.Sizes);

        var newSizes = product.Sizes
            .Where(x => !string.IsNullOrWhiteSpace(x.SizeName))
            .Select(x => new ProductSize
            {
                Id = Guid.NewGuid(),
                ProductId = existingProduct.Id,
                SizeName = x.SizeName,
                Quantity = x.Quantity
            })
            .ToList();

        await _db.ProductSizes.AddRangeAsync(newSizes);

        await _db.SaveChangesAsync();
    }

    public async Task UpdateProductDiscountsAsync(Guid productId, List<Guid> discountIds)
    {
        discountIds = discountIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        var existingLinks = await _db.ProductDiscounts
            .Where(x => x.ProductId == productId)
            .ToListAsync();

        _db.ProductDiscounts.RemoveRange(existingLinks);

        var newLinks = discountIds.Select(discountId => new ProductDiscount
        {
            ProductId = productId,
            DiscountId = discountId
        });

        await _db.ProductDiscounts.AddRangeAsync(newLinks);
        await _db.SaveChangesAsync();
    }
}