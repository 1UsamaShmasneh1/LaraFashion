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

public sealed record CatalogOverview(int ProductCount, decimal MinimumPrice, decimal MaximumPrice, bool HasActiveDiscounts);

public class ProductService
{
    private readonly AppDbContext _db;
    private readonly AppStoragePaths _storagePaths;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductService(AppDbContext db, AppStoragePaths storagePaths, IDbContextFactory<AppDbContext> factory)
    {
        _db = db;
        _storagePaths = storagePaths;
        _factory = factory;
    }

    public async Task<List<Product>> GetActiveProductsAsync(Guid? categoryId = null, string? size = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Sizes)
            .Include(x => x.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.Id))
            .Include(x => x.ProductDiscounts)
                .ThenInclude(x => x.Discount)
            .Include(x => x.ProductCategories)
                .ThenInclude(x => x.Category)
            .Where(x => x.IsActive && x.IsPublished && x.Sizes.Any(s => s.Quantity > 0 && s.SizeName.Trim() != ""));

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(x => x.ProductCategories.Any(pc => pc.CategoryId == categoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(size))
        {
            var normalizedSize = size.Trim().ToUpperInvariant();
            query = query.Where(x => x.Sizes.Any(s =>
                s.Quantity > 0 && s.SizeName.Trim().ToUpper() == normalizedSize));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<CatalogOverview> GetCatalogOverviewAsync(StoreChatMode mode = StoreChatMode.Customer)
    {
        var products = mode == StoreChatMode.Sandbox
            ? await GetUnpublishedProductsAsync()
            : await GetActiveProductsAsync();
        if (products.Count == 0)
            return new(0, 0, 0, false);

        return new(
            products.Count,
            products.Min(x => x.StorePrice),
            products.Max(x => x.StorePrice),
            products.Any(x => x.DiscountType != DiscountType.None || x.ProductDiscounts.Any(pd => pd.Discount.IsActive)));
    }

    public async Task<IReadOnlyList<string>> GetAvailableSizesAsync(StoreChatMode mode = StoreChatMode.Customer)
    {
        var productQuery = _db.Products
            .AsNoTracking()
            .AsQueryable();

        productQuery = mode == StoreChatMode.Sandbox
            ? productQuery.Where(product => !product.IsPublished)
            : productQuery.Where(product => product.IsActive && product.IsPublished);

        var sizeNames = await productQuery
            .SelectMany(product => product.Sizes.Where(size => size.Quantity > 0))
            .Select(size => size.SizeName)
            .ToListAsync();

        var availableSizes = sizeNames
            .Where(size => !string.IsNullOrWhiteSpace(size))
            .Select(SizeLabelParser.NormalizeAvailableLabelForDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        availableSizes.Sort(SizeLabelParser.CompareAvailableLabels);
        return availableSizes;
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

    public async Task<List<Product>> GetUnpublishedProductsAsync(Guid? categoryId = null, string? size = null)
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

        if (!string.IsNullOrWhiteSpace(size))
        {
            var normalizedSize = size.Trim().ToUpperInvariant();
            query = query.Where(x => x.Sizes.Any(s =>
                s.Quantity > 0 && s.SizeName.Trim().ToUpper() == normalizedSize));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<Product?> GetProductAsync(Guid id, bool includeUnpublished = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Products
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

        var removedImageUrls = await SqliteWork.WriteAsync(_factory, async db =>
        {
            var removed = await UpdateProductWithSizesCoreAsync(product, db);
            await ReplaceProductDiscountsAsync(product.Id, discountIds, db);
            await ReplaceProductCategoriesAsync(product.Id, categoryIds, db);
            return removed;
        });

        foreach (var imageUrl in removedImageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    public Task UpdateProductAsync(Product product) => UpdateProductWithSizesAsync(product);

    public async Task DeleteProductAsync(Guid id)
    {
        await DeleteProductInternalAsync(id, requireFinishedQuantities: false);
    }

    private async Task DeleteProductInternalAsync(Guid id, bool requireFinishedQuantities)
    {
        var imageUrls = await SqliteWork.WriteAsync(_factory, async db =>
        {
            var product = await db.Products
                .AsNoTracking()
                .Include(x => x.Sizes)
                .Include(x => x.Images)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (product is null)
                throw new InvalidOperationException("المنتج المطلوب حذفه غير موجود في قاعدة البيانات.");

            var imageUrls = product.Images
                .Select(x => x.ImageUrl)
                .Append(product.ImageUrl)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Recheck both policies inside the DELETE statement, using persisted inventory.
            var deletedProducts = await db.Products
                .Where(x => x.Id == id
                    && (!requireFinishedQuantities || !x.Sizes.Any(s => s.Quantity > 0))
                    && !db.Orders.Any(o => o.Status != OrderStatus.Ready && o.Status != OrderStatus.Cancelled
                        && o.Items.Any(i => i.ProductId == x.Id)))
                .ExecuteDeleteAsync();

            if (deletedProducts == 0)
                throw new InvalidOperationException("تعذر حذف المنتج: يوجد طلب مفتوح أو مخزون يمنع التنظيف، أو تغيرت البيانات.");

            return imageUrls;
        });

        foreach (var imageUrl in imageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    public async Task UpdateProductWithSizesAsync(Product product)
    {
        if (product.Id == Guid.Empty)
            throw new InvalidOperationException("لا يمكن تعديل منتج بدون رقم تعريف.");

        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        var removedImageUrls = await SqliteWork.WriteAsync(_factory, db => UpdateProductWithSizesCoreAsync(product, db));

        foreach (var imageUrl in removedImageUrls)
            await DeleteImageIfUnusedAsync(imageUrl);
    }

    private async Task<List<string>> UpdateProductWithSizesCoreAsync(Product product, AppDbContext db)
    {
        var existingProduct = await db.Products
            .Include(x => x.Sizes)
            .Include(x => x.Images)
            .FirstOrDefaultAsync(x => x.Id == product.Id);

        if (existingProduct is null)
            throw new InvalidOperationException("المنتج المطلوب تعديله غير موجود في قاعدة البيانات.");

        if (product.InventorySnapshot is null || product.InventorySnapshot.Count != existingProduct.Sizes.Count
            || existingProduct.Sizes.Any(x => !product.InventorySnapshot.TryGetValue(x.Id, out var q) || q != x.Quantity))
            throw new InvalidOperationException("تغير مخزون المنتج أو مقاساته منذ فتح التعديل. أعد فتح المنتج ثم احفظ.");
        if (product.Sizes.Any(x => x.Quantity < 0 || string.IsNullOrWhiteSpace(x.SizeName))
            || product.Sizes.Select(x => x.SizeName).Distinct().Count() != product.Sizes.Count)
            throw new InvalidOperationException("المقاسات والكميات غير صالحة أو مكررة.");

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

        // Preserve existing size IDs. Only explicitly removed rows are deleted.
        var removedSizes = existingProduct.Sizes.Where(x => !product.Sizes.Any(s => s.Id == x.Id)).ToList();
        db.ProductSizes.RemoveRange(removedSizes);
        db.ProductImages.RemoveRange(existingProduct.Images);

        var newSizes = product.Sizes
            .Where(x => !string.IsNullOrWhiteSpace(x.SizeName))
            .Where(x => !existingProduct.Sizes.Any(s => s.Id == x.Id))
            .Select(x => new ProductSize
            {
                Id = Guid.NewGuid(),
                ProductId = existingProduct.Id,
                SizeName = x.SizeName,
                Quantity = x.Quantity
            })
            .ToList();

        foreach (var size in existingProduct.Sizes.Except(removedSizes))
        {
            var edited = product.Sizes.Single(x => x.Id == size.Id);
            size.SizeName = edited.SizeName;
            size.Quantity = edited.Quantity;
        }
        await db.ProductSizes.AddRangeAsync(newSizes);

        var newImages = product.Images.Select(x => new ProductImage
        {
            Id = Guid.NewGuid(),
            ProductId = existingProduct.Id,
            ImageUrl = x.ImageUrl,
            SortOrder = x.SortOrder,
            IsPrimary = x.IsPrimary
        }).ToList();

        await db.ProductImages.AddRangeAsync(newImages);

        await db.SaveChangesAsync();

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
            .AsNoTracking()
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
                var validation = await ValidateProductCanBeDeletedAsync(
                    product,
                    requireFinishedQuantities: true);

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
                await DeleteProductInternalAsync(product.Id, requireFinishedQuantities: true);
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

    private async Task<ProductDeleteValidation> ValidateProductCanBeDeletedAsync(
        Product product,
        bool requireFinishedQuantities)
    {
        var allQuantitiesFinished = !product.Sizes.Any() || product.Sizes.All(x => x.Quantity <= 0);

        if (requireFinishedQuantities && !allQuantitiesFinished)
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
                AllQuantitiesFinished: allQuantitiesFinished,
                HasOpenOrders: true,
                Reason: "لا يمكن حذف المنتج لأن هناك طلبيات مرتبطة به ليست بحالة جاهز أو ملغاة.");
        }

        return new ProductDeleteValidation(
            CanDelete: true,
            AllQuantitiesFinished: allQuantitiesFinished,
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

    private async Task ReplaceProductDiscountsAsync(Guid productId, List<Guid> discountIds, AppDbContext? context = null)
    {
        var db = context ?? _db;
        if (productId == Guid.Empty)
            throw new InvalidOperationException("رقم المنتج غير صالح.");

        discountIds = discountIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        var existingLinks = await db.ProductDiscounts
            .Where(x => x.ProductId == productId)
            .ToListAsync();

        db.ProductDiscounts.RemoveRange(existingLinks);

        var newLinks = discountIds.Select(discountId => new ProductDiscount
        {
            ProductId = productId,
            DiscountId = discountId
        });

        await db.ProductDiscounts.AddRangeAsync(newLinks);
    }

    private async Task ReplaceProductCategoriesAsync(Guid productId, List<Guid> categoryIds, AppDbContext? context = null)
    {
        var db = context ?? _db;
        if (productId == Guid.Empty)
            throw new InvalidOperationException("رقم المنتج غير صالح.");

        categoryIds = categoryIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();

        var existingLinks = await db.ProductCategories
            .Where(x => x.ProductId == productId)
            .ToListAsync();

        db.ProductCategories.RemoveRange(existingLinks);

        var newLinks = categoryIds.Select(categoryId => new ProductCategory
        {
            ProductId = productId,
            CategoryId = categoryId
        });

        await db.ProductCategories.AddRangeAsync(newLinks);
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
