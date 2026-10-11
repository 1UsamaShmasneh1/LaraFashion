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
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Product, SemaphoreSlim> CreationGates = new();
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
            .Include(x => x.Sizes.OrderBy(s => s.Id))
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

        var products = await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
        return products.Where(p => p.Sizes.Any(s => SizeNamePolicy.IsSelectable(p, s)
            && (string.IsNullOrWhiteSpace(size) || SizeNamePolicy.Key(s.SizeName) == SizeNamePolicy.Key(size)))).ToList();
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
        var products = mode == StoreChatMode.Sandbox ? await GetUnpublishedProductsAsync() : await GetActiveProductsAsync();
        var sizeNames = products.SelectMany(p => p.Sizes.Where(s => SizeNamePolicy.IsSelectable(p, s))).Select(s => s.SizeName);

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
            .Include(x => x.Sizes.OrderBy(s => s.Id))
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
            .Include(x => x.Sizes.OrderBy(s => s.Id))
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
            var normalizedSize = SizeNamePolicy.Key(size);
            query = query.Where(x => x.Sizes.Any(s =>
                s.Quantity > 0 && s.NormalizedName == normalizedSize));
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
            .Include(x => x.Sizes.OrderBy(s => s.Id))
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
            var product = await GetProductAsync(persistedItem.ProductId, includeUnpublished);

            if (product is null || !product.IsActive || (!product.IsPublished && !includeUnpublished))
            {
                result.Add(new CartItem { ProductId = persistedItem.ProductId, ProductSizeId = persistedItem.ProductSizeId,
                    ProductName = string.IsNullOrEmpty(persistedItem.ProductName) ? "منتج غير متاح" : persistedItem.ProductName,
                    ProductSerialNumber = persistedItem.ProductSerialNumber, ProductImageUrl = persistedItem.ProductImageUrl,
                    Size = persistedItem.Size, Quantity = persistedItem.Quantity, UnitPrice = persistedItem.UnitPrice,
                    OriginalUnitPrice = persistedItem.UnitPrice, ReviewMessage = "هذا المنتج لم يعد متاحاً. احذف هذا العنصر؛ بقية السلة محفوظة." });
                continue;
            }

            ProductSize? productSize = null;
            string? review = null;
            var matches = product.Sizes.Where(s => SizeNamePolicy.Key(s.SizeName) == SizeNamePolicy.Key(persistedItem.Size)).ToList();
            if (persistedItem.ProductSizeId.HasValue)
            {
                productSize = product.Sizes.SingleOrDefault(s => s.Id == persistedItem.ProductSizeId);
                if (productSize is null) review = "المقاس المختار حُذف أو استُبدل. احذف هذا العنصر واختر المقاس من المنتج مجدداً.";
                else if (SizeNamePolicy.Key(productSize.SizeName) != SizeNamePolicy.Key(persistedItem.Size))
                    review = "تغير المقاس المختار. احذف هذا العنصر وأعد اختيار المقاس.";
            }
            else if (matches.Count == 1) productSize = matches[0];
            else review = "تعذر تحديد المقاس القديم بأمان. احذف هذا العنصر وأعد اختياره؛ بقية السلة محفوظة.";
            if (review is null && (productSize is null || !SizeNamePolicy.IsValid(productSize.SizeName) || matches.Count != 1))
                review = "بيانات هذا المقاس غير صالحة أو مكررة. اختر مقاساً آخر أو تواصل مع المتجر.";
            if (review is null && (persistedItem.Quantity <= 0 || persistedItem.Quantity > productSize!.Quantity))
                review = $"الكمية المطلوبة غير متاحة للمقاس {persistedItem.Size}. المتاح: {productSize!.Quantity}. راجع الكمية في السلة.";
            result.Add(new CartItem
            {
                ProductId = product.Id,
                ProductSizeId = persistedItem.ProductSizeId ?? productSize?.Id,
                ReviewMessage = review,
                ProductName = product.Name,
                ProductSerialNumber = product.SerialNumber,
                ProductImageUrl = product.PrimaryImageUrl,
                Size = persistedItem.Size,
                Quantity = persistedItem.Quantity,
                MaxAvailableQuantity = productSize is not null && matches.Count == 1
                    && SizeNamePolicy.Key(productSize.SizeName) == SizeNamePolicy.Key(persistedItem.Size) ? productSize.Quantity : 0,
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
        await AddProductWithRelationsAsync(product, [], []);
    }

    public async Task AddProductWithRelationsAsync(
        Product product,
        List<Guid> discountIds,
        List<Guid> categoryIds)
    {
        var gate = CreationGates.GetValue(product, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await AddProductCoreAsync(product, discountIds, categoryIds); }
        finally { gate.Release(); }
    }

    private async Task AddProductCoreAsync(Product product, List<Guid> discountIds, List<Guid> categoryIds)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
            throw new InvalidOperationException("اسم المنتج مطلوب.");

        if (product.Id != Guid.Empty) throw new InvalidOperationException("هذا المنتج له هوية بالفعل. استخدم تعديل المنتج؛ لن يُنشأ منتج آخر.");
        var originalRows = product.Sizes.ToList();
        var input = SnapshotForWrite(product);
        discountIds = discountIds.ToList(); categoryIds = categoryIds.ToList();
        SizeNamePolicy.Validate(input.Sizes, Guid.Empty);
        var saved = await SqliteWork.WriteAsync(_factory, async db =>
        {
            var ids = input.Sizes.Where(s => s.Id != Guid.Empty).Select(s => s.Id).ToList();
            if (await db.ProductSizes.AnyAsync(s => ids.Contains(s.Id)))
                throw new InvalidOperationException("هوية أحد المقاسات مستخدمة بالفعل. أعد فتح الإضافة.");
            var created = new Product { Id = Guid.NewGuid(), Name = input.Name, SerialNumber = input.SerialNumber,
                Description = input.Description, OriginalPrice = input.OriginalPrice, IsActive = input.IsActive,
                IsPublished = false, ImageUrl = input.ImageUrl,
                Sizes = input.Sizes.Select(s => new ProductSize { Id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
                    SizeName = s.SizeName, Quantity = s.Quantity }).ToList(),
                Images = input.Images.Select(i => new ProductImage { Id = Guid.NewGuid(), ImageUrl = i.ImageUrl,
                    SortOrder = i.SortOrder, IsPrimary = i.IsPrimary }).ToList() };
            NormalizeProductImages(created);
            foreach (var s in created.Sizes) s.ProductId = created.Id;
            db.Products.Add(created);
            await ReplaceProductDiscountsAsync(created.Id, discountIds, db);
            await ReplaceProductCategoriesAsync(created.Id, categoryIds, db);
            return created;
        });
        // Only publish generated identities to the caller after the transaction commits.
        product.Id = saved.Id; product.IsPublished = false; product.CreatedAt = saved.CreatedAt; product.UpdatedAt = saved.UpdatedAt;
        for (var i = 0; i < originalRows.Count; i++)
        { originalRows[i].Id = saved.Sizes[i].Id; originalRows[i].ProductId = saved.Id; }
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

        product = SnapshotForWrite(product);
        discountIds = discountIds.ToList(); categoryIds = categoryIds.ToList();
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

        product = SnapshotForWrite(product);
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
        SizeNamePolicy.Validate(product.Sizes, product.Id);
        var submittedIds = product.Sizes.Where(s => s.Id != Guid.Empty).Select(s => s.Id).ToList();
        if (await db.ProductSizes.AnyAsync(s => submittedIds.Contains(s.Id) && s.ProductId != product.Id)
            || product.Sizes.Any(s => s.ProductId == product.Id && !existingProduct.Sizes.Any(e => e.Id == s.Id)))
            throw new InvalidOperationException("أحد صفوف المقاسات حُذف أو ينتمي لمنتج آخر. أعد فتح التعديل.");

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
                Id = x.Id == Guid.Empty ? Guid.NewGuid() : x.Id,
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

        await SqliteWork.WriteAsync(_factory, async db =>
        {
            var product = await db.Products.Include(p => p.Sizes).SingleOrDefaultAsync(p => p.Id == productId);
            if (product is null) throw new InvalidOperationException("المنتج غير موجود. حدّث الصفحة.");
            SizeNamePolicy.Validate(product.Sizes, product.Id);
            product.IsPublished = true; product.UpdatedAt = DateTime.Now;
            return true;
        });
    }

    public async Task<int> PublishAllUnpublishedProductsAsync()
    {
        return await SqliteWork.WriteAsync(_factory, async db =>
        {
            var products = await db.Products.Include(p => p.Sizes).Where(p => !p.IsPublished).ToListAsync();
            foreach (var product in products) SizeNamePolicy.Validate(product.Sizes, product.Id);
            foreach (var product in products) { product.IsPublished = true; product.UpdatedAt = DateTime.Now; }
            return products.Count;
        });
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

    private static Product SnapshotForWrite(Product p) => new()
    {
        Id = p.Id, Name = p.Name, SerialNumber = p.SerialNumber, Description = p.Description,
        OriginalPrice = p.OriginalPrice, ImageUrl = p.ImageUrl, IsActive = p.IsActive, IsPublished = p.IsPublished,
        InventorySnapshot = p.InventorySnapshot?.ToDictionary(x => x.Key, x => x.Value),
        Sizes = p.Sizes.Select(s => new ProductSize { Id = s.Id, ProductId = s.ProductId, SizeName = s.SizeName, Quantity = s.Quantity }).ToList(),
        Images = p.Images.Select(i => new ProductImage { Id = i.Id, ProductId = i.ProductId, ImageUrl = i.ImageUrl,
            SortOrder = i.SortOrder, IsPrimary = i.IsPrimary }).ToList()
    };

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
