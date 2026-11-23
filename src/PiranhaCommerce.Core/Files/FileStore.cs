using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PiranhaCommerce.Core.Files;

/// <summary>
/// Where uploaded files live (product images, and later dispute evidence). Nothing here knows about disks or clouds: a package registers its own
/// implementation (local disk, Azure Blob, ...) and replaces the default, which refuses every call. File bytes never go in the database.
/// </summary>
/// <remarks>
/// A key is a path made of segments separated by <c>/</c>, each segment letters, digits, <c>.</c>, <c>-</c> or <c>_</c> (and not <c>.</c> or <c>..</c>),
/// for example <c>products/3f2c.../original</c>. A store must refuse any other key. Callers remember a file's content type themselves.
/// </remarks>
public interface IFileStore
{
    /// <summary>Writes the file, replacing any file with that key.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>Opens the file for reading, or null when there is none. The caller disposes the stream.</summary>
    Task<Stream?> OpenAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes every file whose key starts with <c>prefix + "/"</c>. Deleting what is not there is not an error.</summary>
    Task DeleteFolderAsync(string prefix, CancellationToken ct = default);
}

public static class FileKeys
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the key is safe to map to a path or blob name (no traversal, no odd characters).</summary>
    public static string Validate(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 400) throw new ArgumentException("A file key must be 1 to 400 characters.", nameof(key));
        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") throw new ArgumentException("A file key cannot have empty, . or .. segments.", nameof(key));
            foreach (var c in segment)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) throw new ArgumentException("A file key may only use letters, digits, . - and _ in each segment.", nameof(key));
        }
        return key;
    }
}

/// <summary>The default store: there is none. Install a storage package (local disk, Azure Blob).</summary>
internal sealed class NoFileStore : IFileStore
{
    private static InvalidOperationException None() => new("No file store is installed. Add a storage plugin, such as PiranhaCommerce.Storage.LocalDisk.");
    public Task SaveAsync(string key, Stream content, CancellationToken ct = default) => throw None();
    public Task<Stream?> OpenAsync(string key, CancellationToken ct = default) => throw None();
    public Task DeleteFolderAsync(string prefix, CancellationToken ct = default) => throw None();
}

/// <summary>What the image processor learned about, or made from, an image.</summary>
public sealed record ProcessedImage(byte[] Bytes, string ContentType, int Width, int Height);

/// <summary>
/// Reads and resizes images. Optional: with none registered the original is stored as it is and no sizes are made. A package registers its own
/// (SkiaSharp today); a host can bring its own, for example for a licence reason.
/// </summary>
public interface IImageProcessor
{
    /// <summary>The image's size, or null when this is not an image the processor can read.</summary>
    Task<(int Width, int Height)?> ReadSizeAsync(Stream content, CancellationToken ct = default);

    /// <summary>
    /// A copy that fits within <paramref name="maxWidth"/> by <paramref name="maxHeight"/> (never enlarged), turned upright by the photo's own orientation
    /// data, in the same format as the original (GIFs come out as PNG). Null when the image cannot be read.
    /// </summary>
    Task<ProcessedImage?> ResizeAsync(Stream content, int maxWidth, int maxHeight, CancellationToken ct = default);
}

public static class FileStoreServiceCollectionExtensions
{
    /// <summary>Registers the defaults a storage or imaging package replaces by registering its own afterwards.</summary>
    public static IServiceCollection AddPiranhaFileDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileStore, NoFileStore>();
        return services;
    }
}
