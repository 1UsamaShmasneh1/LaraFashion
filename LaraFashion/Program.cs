using LaraFashion.Components;
using LaraFashion.Services;
using LaraFashion.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AdminAuthService>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=Data/larafashion.db"));
builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<BrowserStorageService>();
builder.Services.AddScoped<PasswordHasherService>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<DiscountService>();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
