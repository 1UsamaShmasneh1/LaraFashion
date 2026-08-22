using LaraFashion.Data;
using LaraFashion.Models;
using LaraFashion.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace LaraFashion.Services;

public class ProductCleanupResult
{
    public int TotalProducts { get; set; }
    public int EligibleProducts { get; set; }
    public int DeletedProducts { get; set; }
    public int SkippedProducts { get; set; }
    public int ProductsWithQuantity { get; set; }
    public int ProductsWithOpenOrders { get; set; }
    public int FailedProducts { get; set; }
    public List<string> Messages { get; set; } = new();
}

public class ProductService
{
    private readonly AppDbContext _db;
    private readonly AppStoragePaths _storagePaths;

    public ProductService(AppDbContext db, AppStoragePaths storagePaths)
    {
        _db = db;
        _storagePaths = storagePaths;
    }

    public async Task<List<Product>> GetActiveProductsAsync(Guid? categoryId = null)
    {
        var query = _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Sizes)
            .Include(x => x.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id))
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .Where(x => x.IsActive && x.IsPublished && x.Sizes.Any(s => s.Quantity > 0));

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
            .AsSplitQuery()
            .Include(x => x.Sizes)
            .Include(x => x.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id))
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

    public async Task<List<Product>> GetUnpublishedProductsAsync(Guid? categoryId = null)
    {
        var query = _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Sizes)
            .Include(x => x.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id))
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .Where(x => !x.IsPublished)
            .AsQueryable();

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(x => x.ProductCategories.Any(pc => pc.CategoryId == categoryId.Value));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<Product?> GetProductAsync(Guid id, bool includeUnpublished = false)
    {
        var query = _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Sizes)
            .Include(x => x.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id))
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .AsQueryable();

        if (!includeUnpublished)
        {
            query = query.Where(x => x.IsPublished);
        }

        return await query.FirstOrDefaultAsync(x => x.Id == id);
    }


    public async Task<List<CartItem>> BuildValidCartItemsAsync(
        IEnumerable<PersistedCartItem> persistedItems,
        bool includeUnpublished = false)
    {
        var result = new List<CartItem>();

        foreach (var persistedItem in persistedItems)
        {
            if (persistedItem.ProductId == Guid.Empty || string.IsNullOrWhiteSpace(persistedItem.Size))
                continue;

            var product = await GetProductAsync(persistedItem.ProductId, includeUnpublished);

            if (product is null || !product.IsActive || (!product.IsPublished && !includeUnpublished))
                continue;

            var productSize = product.Sizes
                .FirstOrDefault(x => x.SizeName == persistedItem.Size);

            if (productSize is null || productSize.Quantity <= 0)
                continue;

            var quantity = Math.Min(Math.Max(persistedItem.Quantity, 1), productSize.Quantity);

            result.Add(new CartItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                ProductSerialNumber = product.SerialNumber,
                ProductImageUrl = product.PrimaryImageUrl,
                Size = persistedItem.Size,
                Quantity = quantity,
                MaxAvailableQuantity = productSize.Quantity,
                UnitPrice = product.StorePrice,
                OriginalUnitPrice = product.StoreOriginalPrice,
                ProductDiscounts = product.ProductDiscounts
                    .Where(x => x.Discount.IsActive)
                    .ToList(),
                CategoryNames = product.ProductCategories
                    .Where(x => x.Category.IsActive)
                    .Select(x => x.Category.Name)
                    .ToList()
            });
        }

        return result;
    }

    public async Task AddProductAsync(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        product.Id = Guid.NewGuid();
        product.CreatedAt = DateTime.Now;
        product.UpdatedAt = DateTime.Now;
        product.IsPublished = false;

        product.DiscountType = LaraFashion.Models.Enums.DiscountType.None;
        product.DiscountValue = 0;
        product.ProductDiscounts = new();
        product.ProductCategories = new();

        NormalizeProductImages(product);

        foreach (var size in product.Sizes)
        {
            size.Id = Guid.NewGuid();
            size.ProductId = product.Id;
        }

        _db.Products.Add(product);
        await _db.SaveChangesAsync();
    }

    public async Task AddProductWithRelationsAsync(
        Product product,
        List<Guid> discountIds,
        List<Guid> categoryIds)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        await AddProductAsync(product);
        await ReplaceProductDiscountsAsync(product.Id, discountIds);
        await ReplaceProductCategoriesAsync(product.Id, categoryIds);

        await _db.SaveChangesAsync();

        await transaction.CommitAsync();
    }

    public async Task UpdateProductWithRelationsAsync(
        Product product,
        List<Guid> discountIds,
        List<Guid> categoryIds,
        bool mustExist = true)
    {
        if (product.Id == Guid.Empty)
            throw new InvalidOperationException("لا يمكن تعديل منتج بدون رقم تعريف.");

        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var exists = await _db.Products.AnyAsync(x => x.Id == product.Id);
        var removedImageUrls = new List<string>();

        if (!exists)
        {
            if (mustExist)
                throw new InvalidOperationException("المنتج المطلوب تعديله غير موجود في قاعدة البيانات.");

            await AddProductAsync(product);
        }
        else
        {
            removedImageUrls = await UpdateProductWithSizesCoreAsync(product);
        }

        await ReplaceProductDiscountsAsync(product.Id, discountIds);
        await ReplaceProductCategoriesAsync(product.Id, categoryIds);

        await _db.SaveChangesAsync();

        await transaction.CommitAsync();

        foreach (var imageUrl in removedImageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    public async Task UpdateProductAsync(Product product)
    {
        product.UpdatedAt = DateTime.Now;

        _db.Products.Update(product);

        await _db.SaveChangesAsync();
    }

    public async Task DeleteProductAsync(Guid id)
    {
        await DeleteProductInternalAsync(id, enforceDeleteConditions: true);
    }

    private async Task DeleteProductInternalAsync(Guid id, bool enforceDeleteConditions)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(x => x.Sizes)
            .Include(x => x.Images)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (product is null)
            return;

        if (enforceDeleteConditions)
        {
            var validation = await ValidateProductCanBeDeletedAsync(product);
            if (!validation.CanDelete)
                throw new InvalidOperationException(validation.Reason);
        }

        var imageUrls = product.Images
            .Select(x => x.ImageUrl)
            .Append(product.ImageUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deletedProducts = await _db.Products
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync();

        if (deletedProducts == 0)
            return;

        foreach (var imageUrl in imageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    public async Task UpdateProductWithSizesAsync(Product product)
    {
        if (product.Id == Guid.Empty)
            throw new InvalidOperationException("لا يمكن تعديل منتج بدون رقم تعريف.");

        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var removedImageUrls = await UpdateProductWithSizesCoreAsync(product);
        await transaction.CommitAsync();

        foreach (var imageUrl in removedImageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    private async Task<List<string>> UpdateProductWithSizesCoreAsync(Product product)
    {
        var existingProduct = await _db.Products
            .Include(x => x.Sizes)
            .Include(x => x.Images)
            .FirstOrDefaultAsync(x => x.Id == product.Id);

        if (existingProduct is null)
            return new();

        var oldImageUrls = existingProduct.Images
            .Select(x => x.ImageUrl)
            .Append(existingProduct.ImageUrl)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        NormalizeProductImages(product);

        existingProduct.Name = product.Name;
        existingProduct.SerialNumber = product.SerialNumber;
        existingProduct.Description = product.Description;
        existingProduct.OriginalPrice = product.OriginalPrice;
        existingProduct.DiscountType = LaraFashion.Models.Enums.DiscountType.None;
        existingProduct.DiscountValue = 0;
        existingProduct.ImageUrl = product.PrimaryImageUrl;
        existingProduct.IsActive = product.IsActive;
        existingProduct.UpdatedAt = DateTime.Now;

        _db.ProductSizes.RemoveRange(existingProduct.Sizes);
        _db.ProductImages.RemoveRange(existingProduct.Images);

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

        var newImages = product.Images.Select(x => new ProductImage
        {
            Id = Guid.NewGuid(),
            ProductId = existingProduct.Id,
            ImageUrl = x.ImageUrl,
            SortOrder = x.SortOrder,
            IsPrimary = x.IsPrimary
        }).ToList();

        await _db.ProductImages.AddRangeAsync(newImages);

        await _db.SaveChangesAsync();

        var retainedUrls = newImages
            .Select(x => x.ImageUrl)
            .Append(existingProduct.ImageUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return oldImageUrls
            .Where(x => !retainedUrls.Contains(x))
            .ToList();
    }


    public async Task UpdateProductsDiscountsAsync(List<Guid> productIds, List<Guid> discountIds)
    {
        productIds = productIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        if (!productIds.Any())
            return;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        foreach (var productId in productIds)
        {
            await ReplaceProductDiscountsAsync(productId, discountIds);

            var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId);
            if (product is not null)
            {
                product.DiscountType = LaraFashion.Models.Enums.DiscountType.None;
                product.DiscountValue = 0;
                product.UpdatedAt = DateTime.Now;
            }
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task UpdateProductsCategoriesAsync(List<Guid> productIds, List<Guid> categoryIds)
    {
        productIds = productIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        if (!productIds.Any())
            return;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        foreach (var productId in productIds)
        {
            await ReplaceProductCategoriesAsync(productId, categoryIds);

            var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId);
            if (product is not null)
                product.UpdatedAt = DateTime.Now;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task DeleteProductsAsync(List<Guid> productIds)
    {
        productIds = productIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        foreach (var productId in productIds)
        {
            await DeleteProductAsync(productId);
        }
    }

    public async Task<ProductCleanupResult> CleanDeletableProductsAsync()
    {
        var result = new ProductCleanupResult();

        var products = await _db.Products
            .Include(x => x.Sizes)
            .Include(x => x.Images)
            .Include(x => x.ProductDiscounts)
            .Include(x => x.ProductCategories)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        result.TotalProducts = products.Count;

        foreach (var product in products)
        {
            try
            {
                var validation = await ValidateProductCanBeDeletedAsync(product);

                if (!validation.AllQuantitiesFinished)
                {
                    result.ProductsWithQuantity++;
                    result.SkippedProducts++;
                    continue;
                }

                if (validation.HasOpenOrders)
                {
                    result.ProductsWithOpenOrders++;
                    result.SkippedProducts++;
                    result.Messages.Add($"لم يتم حذف المنتج {product.Name}: توجد طلبيات ليست بحالة جاهز أو ملغاة.");
                    continue;
                }

                result.EligibleProducts++;
                await DeleteProductInternalAsync(product.Id, enforceDeleteConditions: false);
                result.DeletedProducts++;
            }
            catch (Exception ex)
            {
                result.FailedProducts++;
                result.Messages.Add($"فشل حذف المنتج {product.Name}: {ex.Message}");
            }
        }

        return result;
    }

    private async Task<ProductDeleteValidation> ValidateProductCanBeDeletedAsync(Product product)
    {
        var allQuantitiesFinished = !product.Sizes.Any() || product.Sizes.All(x => x.Quantity <= 0);

        if (!allQuantitiesFinished)
        {
            return new ProductDeleteValidation(
                CanDelete: false,
                AllQuantitiesFinished: false,
                HasOpenOrders: false,
                Reason: "لا يمكن حذف المنتج قبل انتهاء جميع الكميات لجميع المقاسات.");
        }

        var hasOpenOrders = await _db.Orders
            .Include(x => x.Items)
            .AnyAsync(order =>
                order.Items.Any(item => item.ProductId == product.Id) &&
                order.Status != OrderStatus.Ready &&
                order.Status != OrderStatus.Cancelled);

        if (hasOpenOrders)
        {
            return new ProductDeleteValidation(
                CanDelete: false,
                AllQuantitiesFinished: true,
                HasOpenOrders: true,
                Reason: "لا يمكن حذف المنتج لأن هناك طلبيات مرتبطة به ليست بحالة جاهز أو ملغاة.");
        }

        return new ProductDeleteValidation(
            CanDelete: true,
            AllQuantitiesFinished: true,
            HasOpenOrders: false,
            Reason: string.Empty);
    }

    private readonly record struct ProductDeleteValidation(
        bool CanDelete,
        bool AllQuantitiesFinished,
        bool HasOpenOrders,
        string Reason);

    public async Task UpdateProductDiscountsAsync(Guid productId, List<Guid> discountIds)
    {
        await ReplaceProductDiscountsAsync(productId, discountIds);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateProductCategoriesAsync(Guid productId, List<Guid> categoryIds)
    {
        await ReplaceProductCategoriesAsync(productId, categoryIds);
        await _db.SaveChangesAsync();
    }

    private async Task ReplaceProductDiscountsAsync(Guid productId, List<Guid> discountIds)
    {
        if (productId == Guid.Empty)
            throw new InvalidOperationException("رقم المنتج غير صالح.");

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
    }

    private async Task ReplaceProductCategoriesAsync(Guid productId, List<Guid> categoryIds)
    {
        if (productId == Guid.Empty)
            throw new InvalidOperationException("رقم المنتج غير صالح.");

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
    }

    public async Task UpdateProductImageAsync(Guid productId, string imageUrl)
    {
        var product = await _db.Products
            .Include(x => x.Images)
            .FirstOrDefaultAsync(x => x.Id == productId);

        if (product is null)
            return;

        var oldImageUrl = product.PrimaryImageUrl;

        var selectedImage = product.Images.FirstOrDefault(x =>
            string.Equals(x.ImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase));

        if (selectedImage is null && !string.IsNullOrWhiteSpace(imageUrl))
        {
            selectedImage = new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                ImageUrl = imageUrl,
                SortOrder = product.Images.Count
            };
            product.Images.Add(selectedImage);
        }

        foreach (var image in product.Images)
            image.IsPrimary = image == selectedImage;

        product.ImageUrl = imageUrl;
        product.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();

        if (!string.Equals(oldImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteImageIfUnusedAsync(oldImageUrl);
        }
    }

    public async Task PublishProductAsync(Guid productId)
    {
        if (productId == Guid.Empty)
            return;

        var product = await _db.Products.FirstOrDefaultAsync(x => x.Id == productId);

        if (product is null || product.IsPublished)
            return;

        product.IsPublished = true;
        product.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();
    }

    public async Task<int> PublishAllUnpublishedProductsAsync()
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var products = await _db.Products
            .Where(x => !x.IsPublished)
            .ToListAsync();

        foreach (var product in products)
        {
            product.IsPublished = true;
            product.UpdatedAt = DateTime.Now;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return products.Count;
    }

    public async Task DeleteImageIfUnusedAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !imageUrl.StartsWith("/uploads/"))
            return;

        var usedByProduct = await _db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByProductImage = await _db.ProductImages.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await _db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);

        if (usedByProduct || usedByProductImage || usedByOrder)
            return;

        var fileName = Path.GetFileName(imageUrl);
        var filePath = Path.Combine(_storagePaths.UploadsPath, fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static void NormalizeProductImages(Product product)
    {
        var images = product.Images
            .Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl))
            .GroupBy(x => x.ImageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToList();

        if (images.Count == 0 && !string.IsNullOrWhiteSpace(product.ImageUrl))
        {
            images.Add(new ProductImage
            {
                ImageUrl = product.ImageUrl,
                IsPrimary = true
            });
        }

        var primary = images.FirstOrDefault(x => x.IsPrimary) ?? images.FirstOrDefault();

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            image.Id = image.Id == Guid.Empty ? Guid.NewGuid() : image.Id;
            image.ProductId = product.Id;
            image.SortOrder = index;
            image.IsPrimary = image == primary;
        }

        product.Images = images;
        product.ImageUrl = primary?.ImageUrl ?? string.Empty;
    }
}
