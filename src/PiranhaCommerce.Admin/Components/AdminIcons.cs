using Microsoft.AspNetCore.Components;

namespace PiranhaCommerce.Admin.Components;

/// <summary>
/// The icons a menu entry can name in <c>AdminMenuItem.Icon</c>: small outline pictures drawn in the text colour, so they follow the theme.
/// A name that is not here shows a plain dot, so a plugin with its own icon name still gets a tidy entry.
/// </summary>
public static class AdminIcons
{
    private static readonly Dictionary<string, string> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = "<path d=\"M3 11l9-8 9 8\"/><path d=\"M5 10v10h5v-6h4v6h5V10\"/>",
        ["box"] = "<path d=\"M12 2.5l8.5 4.5v10L12 21.5 3.5 17V7z\"/><path d=\"M3.5 7L12 11.5 20.5 7\"/><path d=\"M12 11.5v10\"/>",
        ["cart"] = "<path d=\"M3 4h2.2l2.2 11h10.4l2.2-8H6\"/><circle cx=\"9.5\" cy=\"19.5\" r=\"1.3\"/><circle cx=\"17\" cy=\"19.5\" r=\"1.3\"/>",
        ["users"] = "<circle cx=\"9\" cy=\"8\" r=\"3.5\"/><path d=\"M2.5 20c0-3.6 2.9-6 6.5-6s6.5 2.4 6.5 6\"/><circle cx=\"17.5\" cy=\"9\" r=\"2.5\"/><path d=\"M17 14.2c2.7.2 4.5 2 4.5 5.3\"/>",
        ["chart"] = "<path d=\"M4 20V11\"/><path d=\"M10 20V4\"/><path d=\"M16 20v-8\"/><path d=\"M22 20H2\"/>",
        ["mail"] = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2\"/><path d=\"M3 7l9 6 9-6\"/>",
        ["shield"] = "<path d=\"M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z\"/><path d=\"M9 12l2 2 4-4\"/>",
        ["plug"] = "<path d=\"M9 3v5\"/><path d=\"M15 3v5\"/><path d=\"M6 8h12v3a6 6 0 01-12 0z\"/><path d=\"M12 17v4\"/>",
    };

    private const string Dot = "<circle cx=\"12\" cy=\"12\" r=\"3\"/>";

    /// <summary>The icon as an inline SVG, 20 px, aria-hidden (the entry's title is its label).</summary>
    public static MarkupString Svg(string? name) => new(
        "<svg class=\"pc-icon\" viewBox=\"0 0 24 24\" width=\"20\" height=\"20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">"
        + (name is not null && Shapes.TryGetValue(name, out var shape) ? shape : Dot) + "</svg>");
}
