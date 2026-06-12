using LaraFashion.Components;
using LaraFashion.Services;
using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AdminAuthService>();

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

    var fileName = $"{Guid.NewGuid()}{extension.ToLowerInvariant()}";
    var fullPath = Path.Combine(targetUploadsPath, fileName);

    await using (var outputStream = File.Create(fullPath))
    await using (var inputStream = file.OpenReadStream())
    {
        await inputStream.CopyToAsync(outputStream);
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
