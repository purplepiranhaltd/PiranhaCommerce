using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Storage.LocalDisk;

public sealed class LocalDiskOptions
{
    public const string SectionName = "PiranhaCommerce:Storage:LocalDisk";

    /// <summary>The folder files are kept in. A relative path is relative to the application's content root. Back it up, and share it between instances (or use a cloud store) if there are several.</summary>
    public string Path { get; set; } = "App_Data/files";
}

/// <summary>Keeps each file at <c>Path/key</c>. Keys are checked (see <see cref="FileKeys"/>) and the final path must stay inside the folder.</summary>
internal sealed class LocalDiskFileStore(IOptions<LocalDiskOptions> options, IWebHostEnvironment env) : IFileStore
{
    private string Root => System.IO.Path.GetFullPath(System.IO.Path.Combine(env.ContentRootPath, options.Value.Path));

    private string PathFor(string key)
    {
        FileKeys.Validate(key);
        var root = Root;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, key.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("That key is outside the file folder.", nameof(key));
        return full;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Write beside the target, then move: a reader never sees half a file.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }

    public Task DeleteFolderAsync(string prefix, CancellationToken ct = default)
    {
        var path = PathFor(prefix);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }
}

/// <summary>Stores files on the local disk. Configure <see cref="LocalDiskOptions"/>.</summary>
public sealed class LocalDiskStoragePlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Storage.LocalDisk", "Local disk storage", "0.1.0", "Keeps uploaded files in a folder on the server.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<LocalDiskOptions>().BindConfiguration(LocalDiskOptions.SectionName);
        services.AddSingleton<IFileStore, LocalDiskFileStore>(); // registered after the core's refuse-everything default, so this is the store
    }
}
