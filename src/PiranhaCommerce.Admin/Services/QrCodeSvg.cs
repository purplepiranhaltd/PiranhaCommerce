using QRCoder;

namespace PiranhaCommerce.Admin.Services;

/// <summary>Draws text as a QR code, as an inline SVG that scales to its box. Made on the server and placed in the page, so what it encodes never travels in an address.</summary>
public static class QrCodeSvg
{
    public static string Make(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(new System.Drawing.Size(220, 220), "#000000", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
