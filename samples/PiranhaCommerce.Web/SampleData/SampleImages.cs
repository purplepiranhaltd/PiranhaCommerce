using SkiaSharp;

namespace PiranhaCommerce.Web.SampleData;

/// <summary>
/// Draws the sample shop's product pictures (a lipstick in each shade, a foundation bottle, and so on) with SkiaSharp, so the sample needs no image files
/// and uses nobody's photographs. Simple flat illustrations on a soft background.
/// </summary>
internal static class SampleImages
{
    private const int Size = 900;

    public static byte[] Render(string kind, IReadOnlyList<string> colours)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size));
        var canvas = surface.Canvas;
        Background(canvas);

        var shades = colours.Count == 0 ? ["#C0392B"] : colours.ToList();
        if (kind == "swatches") Swatches(canvas, shades);
        else if (kind == "giftset") GiftSet(canvas, shades);
        else
        {
            var shown = kind == "palette" ? shades.Take(1).ToList() : shades.Take(4).ToList(); // a palette is one object that holds all its colours
            var gap = shown.Count == 1 ? 0f : 190f;
            for (var i = 0; i < shown.Count; i++)
            {
                var x = Size / 2f + (i - (shown.Count - 1) / 2f) * gap;
                var colour = SKColor.Parse(shown[i]);
                Shadow(canvas, x, kind is "palette" or "blush" or "highlighter" or "cream" ? 175f : 70f);
                switch (kind)
                {
                    case "lipstick": Lipstick(canvas, x, colour); break;
                    case "gloss": Gloss(canvas, x, colour); break;
                    case "liner": Liner(canvas, x, colour); break;
                    case "mascara": Mascara(canvas, x, colour); break;
                    case "palette": Palette(canvas, x, shades); break;
                    case "foundation": Foundation(canvas, x, colour); break;
                    case "blush": Compact(canvas, x, colour, false); break;
                    case "highlighter": Compact(canvas, x, colour, true); break;
                    case "cleanser": Tube(canvas, x, SKColor.Parse("#F7F2EA"), colour); break;
                    case "spf": Tube(canvas, x, SKColor.Parse("#FFF4E0"), SKColor.Parse("#F29B38")); break;
                    case "cream": Jar(canvas, x, colour); break;
                    case "serum": Dropper(canvas, x, colour); break;
                    default: Lipstick(canvas, x, colour); break;
                }
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    private static SKPaint Fill(SKColor colour) => new() { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPath Polygon(params (float X, float Y)[] points)
    {
        var builder = new SKPathBuilder();
        builder.MoveTo(points[0].X, points[0].Y);
        for (var i = 1; i < points.Length; i++) builder.LineTo(points[i].X, points[i].Y);
        builder.Close();
        return builder.Detach();
    }

    private static SKColor Lighter(SKColor c, float amount) =>
        new((byte)(c.Red + (255 - c.Red) * amount), (byte)(c.Green + (255 - c.Green) * amount), (byte)(c.Blue + (255 - c.Blue) * amount), c.Alpha);

    private static SKColor Darker(SKColor c, float amount) => new((byte)(c.Red * (1 - amount)), (byte)(c.Green * (1 - amount)), (byte)(c.Blue * (1 - amount)), c.Alpha);

    private static void Background(SKCanvas canvas)
    {
        using var sky = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Size, Size), [SKColor.Parse("#FBF5EF"), SKColor.Parse("#F3E3E6")], SKShaderTileMode.Clamp) };
        canvas.DrawRect(0, 0, Size, Size, sky);
        using var halo = Fill(SKColor.Parse("#FFFFFF").WithAlpha(110));
        canvas.DrawCircle(Size * 0.5f, Size * 0.45f, 330, halo);
        using var floor = Fill(SKColor.Parse("#E9D5D6").WithAlpha(150));
        canvas.DrawRect(0, 740, Size, Size - 740, floor);
    }

    private static void Shadow(SKCanvas canvas, float x, float halfWidth)
    {
        using var p = Fill(new SKColor(60, 30, 40, 55));
        canvas.DrawOval(new SKRect(x - halfWidth - 20, 722, x + halfWidth + 20, 758), p);
    }

    private static void Lipstick(SKCanvas canvas, float x, SKColor shade)
    {
        using var case1 = Fill(SKColor.Parse("#231C1F"));
        using var gold = Fill(SKColor.Parse("#C9A24B"));
        using var bullet = Fill(shade);
        using var shine = Fill(Lighter(shade, 0.45f).WithAlpha(150));
        canvas.DrawRoundRect(new SKRect(x - 62, 520, x + 62, 735), 14, 14, case1);
        canvas.DrawRect(x - 62, 500, 124, 36, gold);
        canvas.DrawRect(x - 50, 470, 100, 36, case1);
        canvas.DrawPath(Polygon((x - 46, 480), (x - 46, 380), (x + 46, 322), (x + 46, 480)), bullet);
        canvas.DrawRect(x - 32, 400, 12, 78, shine);
    }

    private static void Gloss(SKCanvas canvas, float x, SKColor shade)
    {
        using var body = Fill(shade.WithAlpha(220));
        using var dark = Fill(SKColor.Parse("#231C1F"));
        using var gold = Fill(SKColor.Parse("#C9A24B"));
        using var shine = Fill(SKColors.White.WithAlpha(120));
        canvas.DrawRoundRect(new SKRect(x - 52, 430, x + 52, 735), 26, 26, body);
        canvas.DrawRect(x - 54, 405, 108, 28, gold);
        canvas.DrawRoundRect(new SKRect(x - 46, 270, x + 46, 410), 20, 20, dark);
        canvas.DrawRoundRect(new SKRect(x - 36, 450, x - 20, 700), 8, 8, shine);
    }

    private static void Liner(SKCanvas canvas, float x, SKColor shade)
    {
        canvas.Save();
        canvas.RotateDegrees(-12, x, 735);
        using var wood = Fill(SKColor.Parse("#E8C9A0"));
        using var body = Fill(shade);
        using var lead = Fill(Darker(shade, 0.2f));
        using var band = Fill(SKColor.Parse("#C9A24B"));
        canvas.DrawRect(x - 24, 360, 48, 375, body);
        canvas.DrawRect(x - 24, 360, 48, 22, band);
        canvas.DrawPath(Polygon((x - 24, 360), (x + 24, 360), (x, 250)), wood);
        canvas.DrawPath(Polygon((x - 9, 282), (x + 9, 282), (x, 250)), lead);
        canvas.Restore();
    }

    private static void Mascara(SKCanvas canvas, float x, SKColor shade)
    {
        using var tube = Fill(shade);
        using var silver = Fill(SKColor.Parse("#B9BCC2"));
        using var dark = Fill(SKColor.Parse("#231C1F"));
        canvas.DrawRoundRect(new SKRect(x - 46, 470, x + 46, 735), 18, 18, tube);
        canvas.DrawRect(x - 48, 500, 96, 22, silver);
        canvas.DrawRoundRect(new SKRect(x - 38, 250, x + 38, 466), 16, 16, dark);
        canvas.DrawRect(x - 4, 190, 8, 70, dark);
        for (var i = 0; i < 6; i++) canvas.DrawRect(x - 14, 160 + i * 10, 28, 5, dark);
    }

    private static void Palette(SKCanvas canvas, float x, IReadOnlyList<string> colours)
    {
        using var shell = Fill(SKColor.Parse("#2A2024"));
        using var rim = Fill(SKColor.Parse("#C9A24B"));
        canvas.DrawRoundRect(new SKRect(x - 290, 400, x + 290, 735), 34, 34, rim);
        canvas.DrawRoundRect(new SKRect(x - 280, 410, x + 280, 725), 28, 28, shell);
        for (var i = 0; i < 8; i++)
        {
            var colour = SKColor.Parse(colours[i % colours.Count]);
            if (i >= colours.Count) colour = Lighter(colour, 0.18f * (i / colours.Count));
            using var pan = Fill(colour);
            var cx = x - 195 + (i % 4) * 130;
            var cy = 500 + (i / 4) * 125;
            canvas.DrawCircle(cx, cy, 52, pan);
            using var gloss = Fill(SKColors.White.WithAlpha(60));
            canvas.DrawCircle(cx - 14, cy - 16, 18, gloss);
        }
    }

    private static void Foundation(SKCanvas canvas, float x, SKColor shade)
    {
        using var glass = Fill(SKColors.White.WithAlpha(230));
        using var liquid = Fill(shade);
        using var dark = Fill(SKColor.Parse("#231C1F"));
        using var gold = Fill(SKColor.Parse("#C9A24B"));
        canvas.DrawRoundRect(new SKRect(x - 100, 430, x + 100, 735), 30, 30, glass);
        canvas.DrawRoundRect(new SKRect(x - 88, 520, x + 88, 722), 22, 22, liquid);
        canvas.DrawRect(x - 36, 396, 72, 40, gold);
        canvas.DrawRoundRect(new SKRect(x - 30, 330, x + 30, 400), 8, 8, dark);
        canvas.DrawRect(x - 4, 290, 8, 44, dark);
        canvas.DrawRect(x + 4, 284, 54, 10, dark);
        using var label = Fill(SKColors.White.WithAlpha(210));
        canvas.DrawRoundRect(new SKRect(x - 50, 590, x + 50, 650), 6, 6, label);
    }

    private static void Compact(SKCanvas canvas, float x, SKColor shade, bool shimmer)
    {
        using var rim = Fill(SKColor.Parse("#C9A24B"));
        using var shell = Fill(SKColor.Parse("#2A2024"));
        using var pan = Fill(shade);
        canvas.DrawCircle(x, 565, 175, rim);
        canvas.DrawCircle(x, 565, 165, shell);
        canvas.DrawCircle(x, 565, 140, pan);
        using var shine = Fill(SKColors.White.WithAlpha((byte)(shimmer ? 120 : 55)));
        canvas.DrawCircle(x - 44, 520, shimmer ? 52 : 34, shine);
        using var sparkle = Fill(SKColors.White.WithAlpha((byte)(shimmer ? 150 : 0)));
        for (var i = 0; i < 7; i++) canvas.DrawCircle(x - 70 + i * 24, 600 + (i % 3) * 14, 5, sparkle);
    }

    private static void Tube(SKCanvas canvas, float x, SKColor body, SKColor band)
    {
        using var shell = Fill(body);
        using var stripe = Fill(band);
        using var cap = Fill(SKColor.Parse("#231C1F"));
        canvas.DrawRoundRect(new SKRect(x - 70, 290, x + 70, 735), 24, 24, shell);
        canvas.DrawRect(x - 70, 520, 140, 90, stripe);
        canvas.DrawRoundRect(new SKRect(x - 44, 235, x + 44, 300), 12, 12, cap);
    }

    private static void Jar(SKCanvas canvas, float x, SKColor accent)
    {
        using var jar = Fill(SKColor.Parse("#FBF6EE"));
        using var lid = Fill(SKColor.Parse("#C9A24B"));
        using var label = Fill(accent.WithAlpha(190));
        canvas.DrawRoundRect(new SKRect(x - 175, 520, x + 175, 735), 36, 36, jar);
        canvas.DrawRoundRect(new SKRect(x - 185, 430, x + 185, 535), 22, 22, lid);
        canvas.DrawRoundRect(new SKRect(x - 80, 590, x + 80, 660), 8, 8, label);
    }

    private static void Dropper(SKCanvas canvas, float x, SKColor accent)
    {
        using var glass = Fill(accent.WithAlpha(235));
        using var dark = Fill(SKColor.Parse("#231C1F"));
        using var rubber = Fill(SKColor.Parse("#3A2A2E"));
        using var shine = Fill(SKColors.White.WithAlpha(90));
        canvas.DrawRoundRect(new SKRect(x - 90, 430, x + 90, 735), 30, 30, glass);
        canvas.DrawRect(x - 44, 395, 88, 40, dark);
        canvas.DrawRoundRect(new SKRect(x - 34, 270, x + 34, 400), 30, 30, rubber);
        canvas.DrawRoundRect(new SKRect(x - 70, 460, x - 52, 700), 8, 8, shine);
    }

    private static void GiftSet(SKCanvas canvas, IReadOnlyList<string> colours)
    {
        Shadow(canvas, Size / 2f, 270);
        using var box = Fill(SKColor.Parse("#E7A9B7"));
        using var lid = Fill(SKColor.Parse("#EFC1CB"));
        using var ribbon = Fill(SKColor.Parse("#C9A24B"));
        canvas.DrawRoundRect(new SKRect(190, 520, 710, 740), 16, 16, box);
        canvas.DrawRoundRect(new SKRect(170, 450, 730, 540), 14, 14, lid);
        canvas.DrawRect(Size / 2f - 34, 450, 68, 290, ribbon);
        canvas.DrawRect(170, 480, 560, 24, ribbon);
        for (var i = 0; i < 3; i++)
        {
            using var bow = Fill(SKColor.Parse("#C9A24B"));
            canvas.DrawCircle(Size / 2f + (i - 1) * 50, 430 - (i == 1 ? 18 : 0), 36, bow);
        }
        for (var i = 0; i < Math.Min(3, colours.Count); i++)
        {
            using var dot = Fill(SKColor.Parse(colours[i]));
            canvas.DrawCircle(250 + i * 38, 610, 15, dot);
        }
    }

    private static void Swatches(SKCanvas canvas, IReadOnlyList<string> colours)
    {
        var columns = Math.Min(4, colours.Count);
        var rows = (int)Math.Ceiling(colours.Count / (double)columns);
        const float cell = 190f;
        for (var i = 0; i < colours.Count; i++)
        {
            var cx = Size / 2f + ((i % columns) - (columns - 1) / 2f) * cell;
            var cy = Size / 2f + ((i / columns) - (rows - 1) / 2f) * cell;
            using var ring = Fill(SKColors.White);
            using var dot = Fill(SKColor.Parse(colours[i]));
            canvas.DrawCircle(cx, cy, 78, ring);
            canvas.DrawCircle(cx, cy, 68, dot);
            using var shine = Fill(SKColors.White.WithAlpha(70));
            canvas.DrawCircle(cx - 22, cy - 24, 20, shine);
        }
    }
}
