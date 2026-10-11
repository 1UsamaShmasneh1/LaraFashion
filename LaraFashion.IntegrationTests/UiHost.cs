using LaraFashion.Components;
using LaraFashion.Data;
using LaraFashion.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using LaraFashion.Models;

static class UiHost
{
    public static async Task<int> Run()
    {
        var scenario = await HistoricalInventoryReviewTests.Historical(quantities: [1, 1]);
        var f = scenario.F;
        await using (var db = f.Db())
        {
            var p = new Product { Name = "كرومي", OriginalPrice = 35, Sizes = new[] { 2, 4, 3, 4 }
                .Select((q, i) => new ProductSize { Id = Guid.NewGuid(), SizeName = $"{i + 1} سنه", Quantity = q }).ToList() };
            await f.Products(db).AddProductWithRelationsAsync(p, [], []); await f.Products(db).PublishProductAsync(p.Id);
            db.AdminUsers.Add(new() { Id = Guid.NewGuid(), Username = "local-ui-test", PasswordHash = new PasswordHasherService().HashPassword("local-ui-test-only") });
            await db.SaveChangesAsync();
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(App).Assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath("LaraFashion"), EnvironmentName = "IntegrationTests"
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(f.Configuration);
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.ClearProviders(); // Never emit configuration, JWTs, or customer fields.
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(f.Paths);
        builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={f.Paths.DatabasePath};Pooling=False"));
        builder.Services.AddScoped<CartService>(); builder.Services.AddScoped<OrderService>(); builder.Services.AddScoped<ProductService>();
        builder.Services.AddScoped<AdminAuthService>(); builder.Services.AddScoped<JwtService>(); builder.Services.AddScoped<PasswordHasherService>();
        builder.Services.AddScoped<BrowserStorageService>(); builder.Services.AddScoped<CategoryService>(); builder.Services.AddScoped<DiscountService>();
        builder.Services.AddScoped<ReportsService>(); builder.Services.AddScoped<ImageMaintenanceService>();
        builder.Services.AddScoped<StoreChatService>(); builder.Services.AddScoped<StoreChatSettingsService>(); builder.Services.AddScoped<StoreChatAgentService>();
        builder.Services.AddScoped<StorefrontFilterState>(); builder.Services.AddScoped<SandboxFilterState>(); builder.Services.AddHttpClient("Groq");
        var app = builder.Build();
        app.UseStaticFiles(); app.UseAntiforgery(); app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        Console.WriteLine($"Isolated UI: http://127.0.0.1:5189 ; SQLite: {f.Paths.DatabasePath}");
        await app.RunAsync("http://127.0.0.1:5189"); return 0;
    }
}
