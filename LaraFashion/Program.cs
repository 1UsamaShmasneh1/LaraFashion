using SkiaSharp;
using LaraFashion.Components;
using LaraFashion.Services;
using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AdminAuthService>();
builder.Services.AddScoped<ReportsService>();
builder.Services.AddScoped<StoreChatService>();
builder.Services.AddScoped<StoreChatSettingsService>();
builder.Services.AddScoped<StoreChatAgentService>();
builder.Services.AddScoped<StorefrontFilterState>();
builder.Services.AddScoped<SandboxFilterState>();
builder.Services.AddHttpClient("Groq", client =>
{
    client.BaseAddress = new Uri("https://api.groq.com/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

var storagePaths = AppStoragePaths.Resolve(builder.Environment);
if (!Path.IsPathFullyQualified(storagePaths.DatabasePath) || !Path.IsPathFullyQualified(storagePaths.UploadsPath))
    throw new InvalidOperationException("Absolute persistent storage paths are required.");
var deploymentCommit = "development";
if (!builder.Environment.IsDevelopment())
{
    if (new[] { "SecretKey", "Issuer", "Audience" }.Any(k => string.IsNullOrWhiteSpace(builder.Configuration[$"JwtSettings:{k}"]))
        || !int.TryParse(builder.Configuration["JwtSettings:ExpiryMinutes"], out var expiry) || expiry <= 0)
        throw new InvalidOperationException("External runtime authentication configuration is required.");
    var release = JsonDocument.Parse(File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "release-version.json")));
    deploymentCommit = release.RootElement.GetProperty("Commit").GetString() ?? "";
    if (!System.Text.RegularExpressions.Regex.IsMatch(deploymentCommit, "^[0-9a-f]{40}$"))
        throw new InvalidOperationException("A valid release manifest is required.");
    if (typeof(AppDbContext).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+').Last() != deploymentCommit)
        throw new InvalidOperationException("Release manifest and application binary differ.");
    using (var binary=File.OpenRead(typeof(AppDbContext).Assembly.Location))
        if (Convert.ToHexString(await SHA256.HashDataAsync(binary)) != release.RootElement.GetProperty("ApplicationSha256").GetString())
            throw new InvalidOperationException("Release binary hash differs from its manifest.");
    var keyPath = Environment.GetEnvironmentVariable("LARAFASHION_DATA_PROTECTION_PATH");
    if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath) || !Directory.Exists(keyPath)
        || Path.GetFullPath(keyPath).StartsWith(Path.GetFullPath(builder.Environment.ContentRootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException("A persistent Data Protection directory outside the release is required.");
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
var dbFolder = Path.GetDirectoryName(storagePaths.DatabasePath)
    ?? throw new InvalidOperationException("The database directory could not be resolved.");

Directory.CreateDirectory(dbFolder);
Directory.CreateDirectory(storagePaths.UploadsPath);

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = storagePaths.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString()));
builder.Services.AddSingleton(storagePaths);

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
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // The maintenance path verifies evidence before closing the review migration.
    // Startup must not bypass that sequence, including in Development.
    await DeploymentReadiness.CheckAsync(storagePaths.DatabasePath, deploymentCommit);

    if (app.Environment.IsDevelopment())
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
    else if (!await db.AdminUsers.AnyAsync())
        throw new InvalidOperationException("An existing production administrator is required; startup cannot create business data.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// The explicit GET readiness probe must return its JSON contract, without redirects.
app.UseWhen(context => context.Request.Path != "/health", branch => branch.UseHttpsRedirection());

// The first transition keeps customer traffic closed until preservation and health checks finish.
var maintenanceFile = Environment.GetEnvironmentVariable("LARAFASHION_MAINTENANCE_FILE");
if (!string.IsNullOrEmpty(maintenanceFile))
{
    if (!Path.IsPathFullyQualified(maintenanceFile)
        || !Path.GetFullPath(maintenanceFile).StartsWith(Path.GetDirectoryName(storagePaths.DatabasePath)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException("The maintenance marker must be outside the release in the persistent data directory.");
    app.Use(async (context, next) =>
    {
        if (File.Exists(maintenanceFile) && context.Request.Path != "/health")
        { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        await next(context);
    });
}

app.UseStaticFiles();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storagePaths.UploadsPath),
    RequestPath = "/uploads"
});

app.UseAntiforgery();

app.MapGet("/health", async () =>
{
    try { return Results.Ok(await DeploymentReadiness.CheckAsync(storagePaths.DatabasePath, deploymentCommit)); }
    catch { return Results.Json(new { ContractVersion = DeploymentReadiness.ContractVersion, Status = "unhealthy" }, statusCode: 503); }
});

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
    AppStoragePaths storagePaths,
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

    const int maxImageSide = 1000;
    const int webpQuality = 82;

    var fileName = $"{Guid.NewGuid()}.webp";
    var fullPath = Path.Combine(storagePaths.UploadsPath, fileName);

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
