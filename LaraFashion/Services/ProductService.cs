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

    public async Task<List<Product>> GetActiveProductsAsync(Guid? categoryId = null)
    {
        var query = _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .Where(x => x.IsActive && x.Sizes.Any(s => s.Quantity > 0));

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(x => x.ProductCategories.Any(pc => pc.CategoryId == categoryId.Value));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Product>> GetAllProductsAsync(Guid? categoryId = null)
    {
        var query = _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .AsQueryable();

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(x => x.ProductCategories.Any(pc => pc.CategoryId == categoryId.Value));
        }

        return await query
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
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task AddProductAsync(Product product)
    {
        product.Id = Guid.NewGuid();
        product.CreatedAt = DateTime.Now;
        product.UpdatedAt = DateTime.Now;

        product.ProductDiscounts = new();
        product.ProductCategories = new();

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
            .Include(x => x.ProductDiscounts)
            .Include(x => x.ProductCategories)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (product is null)
            return;

        var imageUrl = product.ImageUrl;

        _db.ProductDiscounts.RemoveRange(product.ProductDiscounts);
        _db.ProductCategories.RemoveRange(product.ProductCategories);
        _db.ProductSizes.RemoveRange(product.Sizes);
        _db.Products.Remove(product);

        await _db.SaveChangesAsync();

        await DeleteImageIfUnusedAsync(imageUrl);
    }

    public async Task UpdateProductWithSizesAsync(Product product)
    {
        var existingProduct = await _db.Products
            .Include(x => x.Sizes)
            .FirstOrDefaultAsync(x => x.Id == product.Id);

        if (existingProduct is null)
            return;

        var oldImageUrl = existingProduct.ImageUrl;

        existingProduct.Name = product.Name;
        existingProduct.SerialNumber = product.SerialNumber;
        existingProduct.Description = product.Description;
        existingProduct.OriginalPrice = product.OriginalPrice;
        existingProduct.DiscountType = product.DiscountType;
        existingProduct.DiscountValue = product.DiscountValue;
        if (!string.IsNullOrWhiteSpace(product.ImageUrl))
        {
            existingProduct.ImageUrl = product.ImageUrl;
        }
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

        if (!string.Equals(oldImageUrl, existingProduct.ImageUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteImageIfUnusedAsync(oldImageUrl);
        }
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

    public async Task UpdateProductCategoriesAsync(Guid productId, List<Guid> categoryIds)
    {
        categoryIds = categoryIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        var existingLinks = await _db.ProductCategories
            .Where(x => x.ProductId == productId)
            .ToListAsync();

        _db.ProductCategories.RemoveRange(existingLinks);

        var newLinks = categoryIds.Select(categoryId => new ProductCategory
        {
            ProductId = productId,
            CategoryId = categoryId
        });

        await _db.ProductCategories.AddRangeAsync(newLinks);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateProductImageAsync(Guid productId, string imageUrl)
    {
        var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId);

        if (product is null)
            return;

        var oldImageUrl = product.ImageUrl;

        product.ImageUrl = imageUrl;
        product.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();

        if (!string.Equals(oldImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteImageIfUnusedAsync(oldImageUrl);
        }
    }

    public async Task DeleteImageIfUnusedAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/"))
            return;

        var usedByProduct = await _db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await _db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);

        if (usedByProduct || usedByOrder)
            return;

        var fileName = Path.GetFileName(imageUrl);
        var filePath = Path.Combine("/var/www/larafashion/uploads", fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
