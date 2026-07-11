using SkiaSharp;
using LaraFashion.Components;
using LaraFashion.Services;
using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AdminAuthService>();
builder.Services.AddScoped<ReportsService>();

var dbFolder = "/var/www/larafashion/data";
Directory.CreateDirectory(dbFolder);

var dbPath = Path.Combine(dbFolder, "larafashion.db");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<BrowserStorageService>();
builder.Services.AddScoped<PasswordHasherService>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<DiscountService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<ImageMaintenanceService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
var uploadsPath = "/var/www/larafashion/uploads";

Directory.CreateDirectory(uploadsPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/api/store/visit", async (HttpContext context, ReportsService reports, CancellationToken cancellationToken) =>
{
    var userAgent = context.Request.Headers.UserAgent.ToString();
    var botMarkers = new[] { "bot", "crawler", "spider", "slurp", "bingpreview", "facebookexternalhit" };
    if (botMarkers.Any(x => userAgent.Contains(x, StringComparison.OrdinalIgnoreCase))) return Results.NoContent();

    const string cookieName = "lf_visitor";
    var visitorId = context.Request.Cookies[cookieName];
    if (string.IsNullOrWhiteSpace(visitorId) || !Guid.TryParse(visitorId, out _))
    {
        visitorId = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(cookieName, visitorId, new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            IsEssential = true, MaxAge = TimeSpan.FromDays(365)
        });
    }
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visitorId)));
    try { await reports.RecordVisitAsync(hash, cancellationToken); }
    catch (Exception ex) { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("StoreVisits").LogWarning(ex, "Could not record store visit."); }
    return Results.NoContent();
}).DisableAntiforgery();


app.MapPost("/api/admin/upload-product-image", async (
    HttpRequest request,
    IWebHostEnvironment environment,
    LaraFashion.Services.AdminAuthService adminAuthService) =>
{
    var token = request.Headers["X-Admin-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token) || !adminAuthService.IsTokenValid(token))
    {
        return Results.Unauthorized();
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Invalid upload request." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "No image file was selected." });
    }

    if (file.Length > 10 * 1024 * 1024)
    {
        return Results.BadRequest(new { message = "Image is too large. Maximum size is 10 MB." });
    }

    var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    var extension = Path.GetExtension(file.FileName);
    if (string.IsNullOrWhiteSpace(extension) || !allowedExtensions.Contains(extension))
    {
        return Results.BadRequest(new { message = "Only JPG, PNG, and WEBP images are allowed." });
    }

    var targetUploadsPath = "/var/www/larafashion/uploads";
    if (!Directory.Exists("/var/www/larafashion"))
    {
        targetUploadsPath = Path.Combine(environment.WebRootPath, "uploads");
    }

    Directory.CreateDirectory(targetUploadsPath);

    const int maxImageSide = 1000;
    const int webpQuality = 82;

    var fileName = $"{Guid.NewGuid()}.webp";
    var fullPath = Path.Combine(targetUploadsPath, fileName);

    await using var inputStream = file.OpenReadStream();
    using var memoryStream = new MemoryStream();
    await inputStream.CopyToAsync(memoryStream);

    using var originalBitmap = SKBitmap.Decode(memoryStream.ToArray());

    if (originalBitmap is null)
    {
        return Results.BadRequest(new { message = "Invalid image file." });
    }

    var originalWidth = originalBitmap.Width;
    var originalHeight = originalBitmap.Height;

    var scale = Math.Min(
        1.0,
        (double)maxImageSide / Math.Max(originalWidth, originalHeight));

    var targetWidth = Math.Max(1, (int)Math.Round(originalWidth * scale));
    var targetHeight = Math.Max(1, (int)Math.Round(originalHeight * scale));

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
        canvas.DrawBitmap(
            originalBitmap,
            new SKRect(0, 0, targetWidth, targetHeight),
            paint);
    }

    using var image = SKImage.FromBitmap(finalBitmap);
    using var encodedData = image.Encode(SKEncodedImageFormat.Webp, webpQuality);

    if (encodedData is null)
    {
        return Results.BadRequest(new { message = "Could not encode image." });
    }

    await using (var outputStream = File.Create(fullPath))
    {
        encodedData.SaveTo(outputStream);
    }

    return Results.Ok(new
    {
        imageUrl = $"/uploads/{fileName}"
    });
}).DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
