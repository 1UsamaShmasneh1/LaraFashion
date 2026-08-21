namespace LaraFashion.Services;

public sealed record AppStoragePaths(string DatabasePath, string UploadsPath)
{
    public const string ProductionDatabasePath = "/var/www/larafashion/data/larafashion.db";
    public const string ProductionUploadsPath = "/var/www/larafashion/uploads";

    public static AppStoragePaths Resolve(IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
            return new AppStoragePaths(ProductionDatabasePath, ProductionUploadsPath);

        var webRootPath = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");

        return new AppStoragePaths(
            Path.Combine(environment.ContentRootPath, "Data", "larafashion.db"),
            Path.Combine(webRootPath, "uploads"));
    }
}
