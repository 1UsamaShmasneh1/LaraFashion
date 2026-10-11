using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Xml.Linq;

// Reads the existing ring and never runs the application or opens a business database.
static class ProductionKeyProbe
{
    sealed class ReadOnlyRepository(DirectoryInfo directory) : IXmlRepository
    {
        readonly FileSystemXmlRepository source = new(directory, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        public IReadOnlyCollection<XElement> GetAllElements() => source.GetAllElements();
        public void StoreElement(XElement element, string friendlyName) => throw new InvalidOperationException("Key ring writes are forbidden in this probe.");
    }
    public static void Verify(string keys, string contentRoot)
    {
        DeploymentDatabase.Require(OperatingSystem.IsLinux() && Directory.Exists(keys) && Path.IsPathFullyQualified(contentRoot), "InvalidProductionKeyProbePaths");
        var before = Directory.GetFiles(keys).ToDictionary(p => p, p => DeploymentDatabase.Hash(p).GetAwaiter().GetResult());
        DeploymentDatabase.Require(before.Count > 0, "ExistingKeyRingRequired");
        var defaultDirectory = FileSystemXmlRepository.DefaultKeyStorageDirectory;
        DeploymentDatabase.Require(defaultDirectory?.FullName == Path.GetFullPath(keys), "LegacyDefaultKeyDirectoryMismatch");
        WebApplicationBuilder Builder(bool explicitDirectory)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot, EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            var protection = builder.Services.AddDataProtection().DisableAutomaticKeyGeneration();
            if (explicitDirectory) protection.PersistKeysToFileSystem(new DirectoryInfo(keys));
            builder.Services.Configure<KeyManagementOptions>(o => o.XmlRepository = new ReadOnlyRepository(new DirectoryInfo(keys)));
            return builder;
        }
        using var legacy = Builder(false).Build();
        using var candidate = Builder(true).Build();
        var expectedDiscriminator = contentRoot.TrimEnd('/') + "/";
        foreach (var app in new[] { legacy, candidate })
            DeploymentDatabase.Require(app.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator == expectedDiscriminator,
                "ProductionDiscriminatorMismatch");
        const string synthetic = "LaraFashion synthetic continuity probe; no customer payload";
        var oldProtector = legacy.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("LaraFashion.FirstTransition.Continuity.V1");
        var newProtector = candidate.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("LaraFashion.FirstTransition.Continuity.V1");
        DeploymentDatabase.Require(newProtector.Unprotect(oldProtector.Protect(synthetic)) == synthetic
            && oldProtector.Unprotect(newProtector.Protect(synthetic)) == synthetic, "ExistingProductionKeysIncompatible");
        DeploymentDatabase.Require(Directory.GetFiles(keys).Order().SequenceEqual(before.Keys.Order())
            && before.All(p => DeploymentDatabase.Hash(p.Key).GetAwaiter().GetResult() == p.Value), "ProductionKeyRingChanged");
    }
}
