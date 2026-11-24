namespace PiranhaCommerce.Site;

/// <summary>One price band: the domains from <see cref="From"/> to <see cref="To"/> (null for no upper limit) each cost <see cref="Price"/>.</summary>
public sealed record PriceBand(int From, int? To, decimal Price, string Description);

/// <summary>
/// What a Commercial Licence costs. One-off, per domain (a domain and all its subdomains). The price of each domain depends on where it falls in the
/// count of domains held, so buying one more domain never costs more than the one before it. Keep in step with COMMERCIAL-LICENSE.md.
/// </summary>
public static class LicencePricing
{
    public const decimal StandardPrice = 400m;

    public static IReadOnlyList<PriceBand> Bands { get; } =
    [
        new(1, 4, StandardPrice, "Standard price"),
        new(5, 9, 360m, "10% off"),
        new(10, null, 320m, "20% off"),
    ];

    /// <summary>The total for <paramref name="domains"/> domains, each priced by the band it falls in.</summary>
    public static decimal Total(int domains)
    {
        if (domains < 1) throw new ArgumentOutOfRangeException(nameof(domains), "A licence is for at least one domain.");
        decimal total = 0;
        foreach (var band in Bands)
        {
            var upper = band.To is { } to ? Math.Min(domains, to) : domains;
            if (upper >= band.From) total += (upper - band.From + 1) * band.Price;
        }
        return total;
    }

    /// <summary>What each domain costs on average, to show what a bigger order saves.</summary>
    public static decimal AveragePerDomain(int domains) => Total(domains) / domains;
}
