using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Plugins;
using SkiaSharp;

namespace PiranhaCommerce.Imaging.Skia;

/// <summary>Reads and resizes JPEG, PNG, WebP and GIF images with SkiaSharp.</summary>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    /// <summary>Refuses an image whose decoded size would be huge (a small file can describe a gigantic picture).</summary>
    private const long MaxPixels = 100_000_000;
    private const int Quality = 85;

    public async Task<(int Width, int Height)?> ReadSizeAsync(Stream content, CancellationToken ct = default)
    {
        var data = await ReadAllAsync(content, ct);
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null) return null;
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        if ((long)w * h > MaxPixels) return null;
        return Turned(codec.EncodedOrigin) ? (h, w) : (w, h);
    }

    public async Task<ProcessedImage?> ResizeAsync(Stream content, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        var data = await ReadAllAsync(content, ct);
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels) return null;
        var origin = codec.EncodedOrigin;
        var format = codec.EncodedFormat;

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null) return null;
        using var upright = Orient(decoded, origin);

        var scale = Math.Min(1d, Math.Min((double)maxWidth / upright.Width, (double)maxHeight / upright.Height));
        var (w, h) = (Math.Max(1, (int)Math.Round(upright.Width * scale)), Math.Max(1, (int)Math.Round(upright.Height * scale)));
        using var sized = scale < 1d
            ? upright.Resize(new SKImageInfo(w, h, upright.ColorType, upright.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : upright.Copy();
        if (sized is null) return null;

        using var image = SKImage.FromBitmap(sized);
        var (encoding, contentType) = format switch
        {
            SKEncodedImageFormat.Jpeg => (SKEncodedImageFormat.Jpeg, "image/jpeg"),
            SKEncodedImageFormat.Webp => (SKEncodedImageFormat.Webp, "image/webp"),
            _ => (SKEncodedImageFormat.Png, "image/png"), // PNG and GIF
        };
        using var encoded = image.Encode(encoding, Quality);
        return encoded is null ? null : new ProcessedImage(encoded.ToArray(), contentType, sized.Width, sized.Height);
    }

    private static async Task<SKData> ReadAllAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return SKData.CreateCopy(buffer.ToArray());
    }

    private static bool Turned(SKEncodedOrigin o) => o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Turns a photo upright from its camera orientation (EXIF) so the stored copy needs no such data.</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return src.Copy();
        float w = src.Width, h = src.Height;
        var turned = Turned(origin);
        var dst = new SKBitmap(turned ? src.Height : src.Width, turned ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix { ScaleX = -1, TransX = w, ScaleY = 1, Persp2 = 1 },
            SKEncodedOrigin.BottomRight => new SKMatrix { ScaleX = -1, TransX = w, ScaleY = -1, TransY = h, Persp2 = 1 },
            SKEncodedOrigin.BottomLeft => new SKMatrix { ScaleX = 1, ScaleY = -1, TransY = h, Persp2 = 1 },
            SKEncodedOrigin.LeftTop => new SKMatrix { SkewX = 1, SkewY = 1, Persp2 = 1 },
            SKEncodedOrigin.RightTop => new SKMatrix { SkewX = -1, TransX = h, SkewY = 1, Persp2 = 1 },
            SKEncodedOrigin.RightBottom => new SKMatrix { SkewX = -1, TransX = h, SkewY = -1, TransY = w, Persp2 = 1 },
            _ => new SKMatrix { SkewX = 1, SkewY = -1, TransY = w, Persp2 = 1 }, // LeftBottom
        });
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return dst;
    }
}

/// <summary>Reads and resizes images with SkiaSharp. Add it so product images get sizes made when they are uploaded.</summary>
public sealed class SkiaImagingPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Imaging.Skia", "SkiaSharp imaging", "0.1.0", "Makes sizes of product images with SkiaSharp.");

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
}
