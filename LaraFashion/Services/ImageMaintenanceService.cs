using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace LaraFashion.Services;

public class ImageMaintenanceResult
{
    public int TotalFiles { get; set; }
    public int UsedImages { get; set; }
    public int DeletedUnusedFiles { get; set; }
    public int ConvertedImages { get; set; }
    public int MissingUsedFiles { get; set; }
    public int FailedFiles { get; set; }
    public List<string> Messages { get; set; } = new();
}

public class ImageMaintenanceService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public ImageMaintenanceService(AppDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    public async Task<ImageMaintenanceResult> CleanAndNormalizeImagesAsync()
    {
        var result = new ImageMaintenanceResult();
        var uploadsPath = GetUploadsPath();
        Directory.CreateDirectory(uploadsPath);

        var files = Directory.GetFiles(uploadsPath);
        result.TotalFiles = files.Length;

        var usedImageUrls = await GetUsedImageUrlsAsync();
        result.UsedImages = usedImageUrls.Count;

        var usedFileNames = usedImageUrls
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);

            if (!usedFileNames.Contains(fileName))
            {
                try
                {
                    File.Delete(filePath);
                    result.DeletedUnusedFiles++;
                }
                catch (Exception ex)
                {
                    result.FailedFiles++;
                    result.Messages.Add($"فشل حذف صورة غير مستخدمة: {fileName} - {ex.Message}");
                }
            }
        }

        foreach (var imageUrl in usedImageUrls)
        {
            var fileName = Path.GetFileName(imageUrl);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var oldPath = Path.Combine(uploadsPath, fileName);
            if (!File.Exists(oldPath))
            {
                result.MissingUsedFiles++;
                result.Messages.Add($"صورة مستخدمة لكنها غير موجودة على السيرفر: {imageUrl}");
                continue;
            }

            var extension = Path.GetExtension(fileName);
            if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var newFileName = $"{Guid.NewGuid()}.webp";
                var newPath = Path.Combine(uploadsPath, newFileName);
                var newImageUrl = $"/uploads/{newFileName}";

                await ConvertToSmallWebpAsync(oldPath, newPath);
                await ReplaceImageUrlInDatabaseAsync(imageUrl, newImageUrl);

                if (File.Exists(oldPath) && !await IsImageUrlUsedAsync(imageUrl))
                {
                    File.Delete(oldPath);
                }

                result.ConvertedImages++;
            }
            catch (Exception ex)
            {
                result.FailedFiles++;
                result.Messages.Add($"فشل تحويل الصورة: {imageUrl} - {ex.Message}");
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    private string GetUploadsPath()
    {
        var productionPath = "/var/www/larafashion/uploads";
        if (Directory.Exists("/var/www/larafashion"))
            return productionPath;

        return Path.Combine(_environment.WebRootPath, "uploads");
    }

    private async Task<HashSet<string>> GetUsedImageUrlsAsync()
    {
        var productUrls = await _db.Products
            .Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl) && x.ImageUrl.StartsWith("/uploads/"))
            .Select(x => x.ImageUrl)
            .ToListAsync();

        var orderUrls = await _db.OrderItems
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductImageUrl) && x.ProductImageUrl.StartsWith("/uploads/"))
            .Select(x => x.ProductImageUrl)
            .ToListAsync();

        return productUrls
            .Concat(orderUrls)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task ReplaceImageUrlInDatabaseAsync(string oldUrl, string newUrl)
    {
        var products = await _db.Products
            .Where(x => x.ImageUrl == oldUrl)
            .ToListAsync();

        foreach (var product in products)
        {
            product.ImageUrl = newUrl;
            product.UpdatedAt = DateTime.Now;
        }

        var orderItems = await _db.OrderItems
            .Where(x => x.ProductImageUrl == oldUrl)
            .ToListAsync();

        foreach (var orderItem in orderItems)
        {
            orderItem.ProductImageUrl = newUrl;
        }
    }

    private async Task<bool> IsImageUrlUsedAsync(string imageUrl)
    {
        var usedByProduct = await _db.Products.AnyAsync(x => x.ImageUrl == imageUrl);
        var usedByOrder = await _db.OrderItems.AnyAsync(x => x.ProductImageUrl == imageUrl);
        return usedByProduct || usedByOrder;
    }

    private static async Task ConvertToSmallWebpAsync(string sourcePath, string destinationPath)
    {
        var originalBytes = await File.ReadAllBytesAsync(sourcePath);
        using var originalBitmap = SKBitmap.Decode(originalBytes);

        if (originalBitmap is null)
            throw new InvalidOperationException("الملف ليس صورة صالحة.");

        const int maxImageSide = 1000;
        const int webpQuality = 82;

        var scale = Math.Min(
            1.0,
            (double)maxImageSide / Math.Max(originalBitmap.Width, originalBitmap.Height));

        var targetWidth = Math.Max(1, (int)Math.Round(originalBitmap.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(originalBitmap.Height * scale));

        using var finalBitmap = new SKBitmap(
            new SKImageInfo(targetWidth, targetHeight, originalBitmap.ColorType, originalBitmap.AlphaType));

        using (var canvas = new SKCanvas(finalBitmap))
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(originalBitmap, new SKRect(0, 0, targetWidth, targetHeight), paint);
        }

        using var image = SKImage.FromBitmap(finalBitmap);
        using var encodedData = image.Encode(SKEncodedImageFormat.Webp, webpQuality);

        if (encodedData is null)
            throw new InvalidOperationException("فشل ضغط الصورة إلى WebP.");

        await using var outputStream = File.Create(destinationPath);
        encodedData.SaveTo(outputStream);
    }
}
