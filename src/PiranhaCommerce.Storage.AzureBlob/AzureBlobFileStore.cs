using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Storage.AzureBlob;

public sealed class AzureBlobOptions
{
    public const string SectionName = "PiranhaCommerce:Storage:AzureBlob";

    /// <summary>A storage account connection string. Leave empty to sign in with <see cref="AccountUri"/> instead.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The account address (https://name.blob.core.windows.net), used with the host's Azure identity (a managed identity in Azure, or the developer's own sign-in locally). It needs the Storage Blob Data Contributor role.</summary>
    public string? AccountUri { get; set; }

    /// <summary>The container files go in. It is created if it is missing and the identity may create it. Keep it private: pictures are served through the API.</summary>
    public string Container { get; set; } = "piranhacommerce-files";
}

/// <summary>Keeps each file as a block blob named by its key, in one private container.</summary>
internal sealed class AzureBlobFileStore(IOptions<AzureBlobOptions> options) : IFileStore
{
    private readonly SemaphoreSlim _ready = new(1, 1);
    private BlobContainerClient? _container;

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        if (_container is not null) return _container;
        await _ready.WaitAsync(ct);
        try
        {
            if (_container is not null) return _container;
            var o = options.Value;
            var client = !string.IsNullOrWhiteSpace(o.ConnectionString)
                ? new BlobContainerClient(o.ConnectionString, o.Container)
                : !string.IsNullOrWhiteSpace(o.AccountUri)
                    ? new BlobContainerClient(new Uri(new Uri(o.AccountUri), o.Container), new DefaultAzureCredential())
                    : throw new InvalidOperationException("Set PiranhaCommerce:Storage:AzureBlob:ConnectionString, or AccountUri to use the host's Azure identity.");
            await client.CreateIfNotExistsAsync(cancellationToken: ct);
            return _container = client;
        }
        finally { _ready.Release(); }
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        FileKeys.Validate(key);
        await (await ContainerAsync(ct)).GetBlobClient(key).UploadAsync(content, overwrite: true, ct);
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        FileKeys.Validate(key);
        try { return await (await ContainerAsync(ct)).GetBlobClient(key).OpenReadAsync(cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task DeleteFolderAsync(string prefix, CancellationToken ct = default)
    {
        FileKeys.Validate(prefix);
        var container = await ContainerAsync(ct);
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix + "/", ct))
            await container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
    }
}

/// <summary>Stores files in Azure Blob Storage. Configure <see cref="AzureBlobOptions"/>. Use instead of the local disk package, not with it.</summary>
public sealed class AzureBlobStoragePlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Storage.AzureBlob", "Azure Blob storage", "0.1.0", "Keeps uploaded files in an Azure storage container.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<AzureBlobOptions>().BindConfiguration(AzureBlobOptions.SectionName);
        services.AddSingleton<IFileStore, AzureBlobFileStore>(); // registered after the core's refuse-everything default, so this is the store
    }
}
