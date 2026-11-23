using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Catalog;

/// <summary>A scannable code for a product. A product can have several; a code identifies at most one product.</summary>
[Audited]
public class ProductBarcode : Entity
{
    public Guid ProductId { get; set; }

    /// <summary>The code as entered (trimmed).</summary>
    public required string Code { get; set; }

    /// <summary>Canonical form used for matching and uniqueness; see <see cref="BarcodeNormalizer"/>.</summary>
    public required string NormalizedCode { get; set; }
}

/// <summary>
/// Canonical form of a barcode so the same product code scanned in different symbologies matches.
/// 8/12/13/14-digit numeric codes are GTINs: the check digit is validated and the code is left-padded to 14 digits,
/// so UPC-A <c>012345678905</c> equals EAN-13 <c>0012345678905</c>. Any other code (e.g. Code 128) is trimmed and upper-cased.
/// </summary>
public static class BarcodeNormalizer
{
    public const int MaxLength = 100;

    /// <summary>Throws <see cref="PiranhaValidationException"/> for empty, over-long or bad-check-digit codes.</summary>
    public static string Normalize(string? code, string field = "Code")
    {
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c))
            throw new PiranhaValidationException(field, "A barcode cannot be empty.");
        if (c.Length > MaxLength)
            throw new PiranhaValidationException(field, $"A barcode can be at most {MaxLength} characters.");

        if (c.All(char.IsAsciiDigit) && c.Length is 8 or 12 or 13 or 14)
        {
            if (!HasValidCheckDigit(c))
                throw new PiranhaValidationException(field, $"'{c}' is not a valid GTIN: the check digit is wrong.");
            return c.PadLeft(14, '0');
        }
        return c.ToUpperInvariant();
    }

    /// <summary>Mod-10 check, identical for GTIN-8/12/13/14: weights 3,1,3,1... from the digit left of the check digit.</summary>
    public static bool HasValidCheckDigit(string digits)
    {
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
            sum += (digits[i] - '0') * ((digits.Length - 2 - i) % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }
}

public sealed class ProductBarcodesInput
{
    public List<string> Codes { get; set; } = [];
}

public sealed record ProductBarcodeDto(Guid Id, string Code, string NormalizedCode);

public sealed record ProductBarcodesDto(Guid ProductId, IReadOnlyList<ProductBarcodeDto> Barcodes);
