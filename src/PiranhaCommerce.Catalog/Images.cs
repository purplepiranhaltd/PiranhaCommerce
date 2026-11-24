using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>A picture of a product (or of a variant). The bytes live in the <see cref="IFileStore"/>; this row says what they are and where they sit in the gallery.</summary>
[Audited]
public class ProductImage : Entity
{
    public Guid ProductId { get; set; }
    public required string ContentType { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Null when no image processor is installed (nothing was able to read it).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? AltText { get; set; }

    /// <summary>0 is the main image.</summary>
    public int Position { get; set; }

    /// <summary>The sizes made at upload, as comma separated longest sides (for example <c>160,480</c>). Empty when none were.</summary>
    public string Sizes { get; set; } = "";

    /// <summary>The content type of the made sizes (the original's, except a GIF comes out as PNG).</summary>
    public string? SizeContentType { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class ImageOptions
{
    public const string SectionName = "PiranhaCommerce:Images";

    /// <summary>The biggest upload accepted, in bytes (default 10 MB).</summary>
    [Range(1024, 200_000_000)]
    public int MaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Images one product can have.</summary>
    [Range(1, 500)]
    public int MaxPerProduct { get; set; } = 20;

    /// <summary>Sizes to make at upload, as comma separated longest sides in pixels. An image is never enlarged. Needs an image processor package.</summary>
    public string Sizes { get; set; } = "160,480,1024";

    public IReadOnlyList<int> SizeList() => Sizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n is >= 16 and <= 8000).Distinct().Order().ToList();
}

public sealed class ProductImageInput
{
    [StringLength(200)]
    public string? AltText { get; set; }
}

public sealed class ProductImageOrderInput
{
    /// <summary>Every image of the product, in the order wanted; the first is the main image.</summary>
    public List<Guid> ImageIds { get; set; } = [];
}

/// <param name="Url">Where a shop front fetches the picture (anonymous, cacheable): add <c>?width=</c> for the smallest made size at least that big.</param>
public sealed record ProductImageDto(
    Guid Id, Guid ProductId, string Url, string? AltText, string? FileName, string ContentType, long SizeBytes, int? Width, int? Height, int Position, IReadOnlyList<int> Sizes);

/// <param name="Inherited">True when the product is a variant with no pictures of its own, so these are its parent's.</param>
public sealed record ProductImagesDto(Guid ProductId, bool Inherited, IReadOnlyList<ProductImageDto> Images);

/// <summary>Where product pictures are fetched from (add the picture id, and optionally <c>?width=</c>).</summary>
public static class ProductImageUrls
{
    public const string Prefix = "/api/v1/storefront/guest/product-images/";
}

/// <summary>A picture ready to send. The caller disposes <see cref="Content"/>.</summary>
public sealed record ImageFile(Stream Content, string ContentType, string ETag);

public interface IProductImageService
{
    /// <summary>Null when the product does not exist.</summary>
    Task<ProductImagesDto?> ListAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. A validation error for a file that is too big, not an image, or one too many.</summary>
    Task<ProductImageDto?> AddAsync(Guid productId, Stream content, string? fileName, string? altText, CancellationToken ct = default);

    Task<ProductImageDto?> UpdateAsync(Guid productId, Guid imageId, ProductImageInput input, CancellationToken ct = default);
    Task<ProductImagesDto?> ReorderAsync(Guid productId, IReadOnlyCollection<Guid> imageIds, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid productId, Guid imageId, CancellationToken ct = default);

    /// <summary>The picture (at the smallest made size that is at least <paramref name="width"/> wide, else the original), or null.</summary>
    Task<ImageFile?> OpenAsync(Guid imageId, int? width, CancellationToken ct = default);
}

internal sealed class ProductImageService(PiranhaDbContext db, IFileStore store, IOptions<ImageOptions> options, IImageProcessor? processor = null) : IProductImageService
{
    public const string UrlPrefix = ProductImageUrls.Prefix;

    private static string Folder(Guid productId, Guid imageId) => $"products/{productId}/{imageId}";

    public async Task<ProductImagesDto?> ListAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var own = await db.Set<ProductImage>().AsNoTracking().Where(i => i.ProductId == productId).OrderBy(i => i.Position).ThenBy(i => i.CreatedUtc).ToListAsync(ct);
        if (own.Count > 0) return new ProductImagesDto(productId, false, own.Select(ToDto).ToList());

        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        if (parentId is null) return new ProductImagesDto(productId, false, []);
        var inherited = await db.Set<ProductImage>().AsNoTracking().Where(i => i.ProductId == parentId).OrderBy(i => i.Position).ThenBy(i => i.CreatedUtc).ToListAsync(ct);
        return new ProductImagesDto(productId, inherited.Count > 0, inherited.Select(ToDto).ToList());
    }

    public async Task<ProductImageDto?> AddAsync(Guid productId, Stream content, string? fileName, string? altText, CancellationToken ct = default)
    {
        var opts = options.Value;
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        if (altText is { Length: > 200 }) throw new PiranhaValidationException("altText", "Alt text can be at most 200 characters.");
        var existing = await db.Set<ProductImage>().Where(i => i.ProductId == productId).Select(i => i.Position).ToListAsync(ct);
        if (existing.Count >= opts.MaxPerProduct) throw new PiranhaValidationException("file", $"A product can have at most {opts.MaxPerProduct} images.");

        // Read at most one byte more than allowed, so an oversized upload is refused without holding all of it.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > opts.MaxBytes) throw new PiranhaValidationException("file", $"An image can be at most {opts.MaxBytes / (1024 * 1024d):0.#} MB.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();

        // The type comes from the file's own bytes, never from its name or what the browser claims (SVG is refused: it can carry script).
        var type = Sniff(bytes) ?? throw new PiranhaValidationException("file", "Upload a JPEG, PNG, WebP or GIF image.");

        int? width = null, height = null;
        var sizes = new List<(int Size, ProcessedImage Image)>();
        if (processor is not null)
        {
            var info = await processor.ReadSizeAsync(new MemoryStream(bytes), ct) ?? throw new PiranhaValidationException("file", "That file is not an image that can be read.");
            (width, height) = info;
            foreach (var s in opts.SizeList().Where(s => s < Math.Max(info.Width, info.Height)))
                if (await processor.ResizeAsync(new MemoryStream(bytes), s, s, ct) is { } made) sizes.Add((s, made));
        }

        var image = new ProductImage
        {
            ProductId = productId, ContentType = type, FileName = CleanName(fileName), SizeBytes = bytes.Length, Width = width, Height = height,
            AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim(), Position = existing.Count == 0 ? 0 : existing.Max() + 1,
            Sizes = string.Join(',', sizes.Select(s => s.Size)), SizeContentType = sizes.Count > 0 ? sizes[0].Image.ContentType : null, CreatedUtc = DateTimeOffset.UtcNow,
        };
        var folder = Folder(productId, image.Id);
        try
        {
            await store.SaveAsync(folder + "/original", new MemoryStream(bytes), ct);
            foreach (var (size, made) in sizes) await store.SaveAsync($"{folder}/w{size}", new MemoryStream(made.Bytes), ct);
            db.Add(image);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            try { await store.DeleteFolderAsync(folder, CancellationToken.None); } catch { /* the original failure is the one to report */ }
            throw;
        }
        return ToDto(image);
    }

    public async Task<ProductImageDto?> UpdateAsync(Guid productId, Guid imageId, ProductImageInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var image = await db.Set<ProductImage>().FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, ct);
        if (image is null) return null;
        image.AltText = string.IsNullOrWhiteSpace(input.AltText) ? null : input.AltText.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(image);
    }

    public async Task<ProductImagesDto?> ReorderAsync(Guid productId, IReadOnlyCollection<Guid> imageIds, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var images = await db.Set<ProductImage>().Where(i => i.ProductId == productId).ToListAsync(ct);
        var wanted = imageIds.ToList();
        if (wanted.Count != images.Count || wanted.Distinct().Count() != wanted.Count || images.Any(i => !wanted.Contains(i.Id)))
            throw new PiranhaValidationException("imageIds", "List every image of this product exactly once.");
        for (var i = 0; i < wanted.Count; i++) images.Single(x => x.Id == wanted[i]).Position = i;
        await db.SaveChangesAsync(ct);
        return await ListAsync(productId, ct);
    }

    public async Task<bool> DeleteAsync(Guid productId, Guid imageId, CancellationToken ct = default)
    {
        var image = await db.Set<ProductImage>().FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, ct);
        if (image is null) return false;
        var removed = await db.DeleteTrackedAsync<ProductImage>(i => i.Id == imageId, ct) > 0;
        // The row goes first: a failure here leaves a stray file, never a picture that points at nothing.
        if (removed) await store.DeleteFolderAsync(Folder(productId, imageId), ct);
        return removed;
    }

    public async Task<ImageFile?> OpenAsync(Guid imageId, int? width, CancellationToken ct = default)
    {
        var image = await db.Set<ProductImage>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return null;
        var made = image.Sizes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToList();
        var chosen = width is { } w ? made.Where(s => s >= w).Select(s => (int?)s).FirstOrDefault() : null;
        var key = Folder(image.ProductId, image.Id) + (chosen is { } c ? $"/w{c}" : "/original");
        var stream = await store.OpenAsync(key, ct);
        return stream is null ? null : new ImageFile(stream, chosen is null ? image.ContentType : image.SizeContentType ?? image.ContentType, $"\"{image.Id:N}-{chosen?.ToString() ?? "o"}\"");
    }

    private static ProductImageDto ToDto(ProductImage i) => new(
        i.Id, i.ProductId, UrlPrefix + i.Id, i.AltText, i.FileName, i.ContentType, i.SizeBytes, i.Width, i.Height, i.Position,
        i.Sizes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList());

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(Path.GetFileName(name.Trim()).Where(c => !char.IsControl(c)).ToArray());
        return clean.Length == 0 ? null : clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>JPEG, PNG, GIF or WebP by their opening bytes; null for anything else.</summary>
    internal static string? Sniff(byte[] b) => ImageTypes.Sniff(b);
}

/// <summary>Telling image types apart by their own bytes, for any feature that takes uploads.</summary>
public static class ImageTypes
{
    /// <summary>JPEG, PNG, GIF or WebP by their opening bytes; null for anything else (SVG is refused: it can carry script).</summary>
    public static string? Sniff(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }
}

public static class Seo
{
    /// <summary>Lower-cases and trims a slug; null when empty. A validation error unless it is lower case letters and digits joined by single hyphens.</summary>
    public static string? NormalizeSlug(string? slug, string field = "Slug")
    {
        var s = slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > 200 || !System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            throw new PiranhaValidationException(field, "Use lower case letters and digits, with single hyphens between words (for example blue-cotton-tee).");
        return s;
    }
}
