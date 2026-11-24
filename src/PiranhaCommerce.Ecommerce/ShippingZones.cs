using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A named part of the world a shipping method prices as one: some countries, or only some postcodes of a country (the Highlands and Islands, Northern Ireland,
/// the Channel Islands). A destination belongs to exactly one zone; shipping rates can be set per zone instead of per country.
/// </summary>
[Audited]
public class ShippingZone : AuditableEntity
{
    public required string Name { get; set; }
    public int Position { get; set; }
}

/// <summary>
/// One country, or a few of its postcode areas, in a zone. <see cref="Prefix"/> is empty for the whole country. A (country, prefix) pair belongs to one zone only,
/// so a destination is never in two. The country <c>*</c> is "everywhere not named in another zone".
/// </summary>
public class ShippingZoneRegion : Entity
{
    public Guid ZoneId { get; set; }
    public required string CountryCode { get; set; }

    /// <summary>Upper case, no spaces; empty for the whole country.</summary>
    public string Prefix { get; set; } = "";
}

public sealed class ShippingZoneRegionInput
{
    [Required, RegularExpression(@"^(\*|[A-Za-z]{2})$", ErrorMessage = "Use a two-letter country code such as GB, or * for everywhere else.")]
    public string CountryCode { get; set; } = "";

    /// <summary>
    /// Postcode starts, such as <c>BT</c>, <c>IV</c> or <c>HS</c>. Empty means the whole country. A start made of letters only matches a postcode where a digit
    /// follows it, so <c>B</c> means Birmingham (B1, B23) and not BA, BB or BT; write <c>BT</c> for Belfast.
    /// </summary>
    [MaxLength(200)] public List<string> PostcodePrefixes { get; set; } = [];
}

public sealed class ShippingZoneInput
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";
    [Range(0, 10_000)] public int Position { get; set; }
    [MaxLength(300)] public List<ShippingZoneRegionInput> Regions { get; set; } = [];
}

public sealed record ShippingZoneRegionDto(string CountryCode, IReadOnlyList<string> PostcodePrefixes);

public sealed record ShippingZoneDto(Guid Id, string Name, int Position, IReadOnlyList<ShippingZoneRegionDto> Regions, int RateCount = 0);

public static class ShippingZoneRules
{
    public const string Everywhere = "*";

    /// <summary>A postcode as compared: upper case with no spaces or hyphens. Empty when none was given.</summary>
    public static string NormalizePostcode(string? postcode) =>
        new((postcode ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Whether a (normalised) prefix covers a (normalised) postcode. See <see cref="ShippingZoneRegionInput.PostcodePrefixes"/> for the rule about letters-only prefixes.</summary>
    public static bool PrefixMatches(string prefix, string postcode)
    {
        if (prefix.Length == 0) return true;
        if (!postcode.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return !prefix.All(char.IsLetter) || (postcode.Length > prefix.Length && char.IsDigit(postcode[prefix.Length]));
    }

    /// <summary>
    /// The zone a destination belongs to, or null. The longest postcode start that matches wins, then a zone holding the whole country, then the zone for
    /// everywhere else. Without a postcode only a whole country (or everywhere) can match.
    /// </summary>
    public static Guid? Resolve(IReadOnlyList<ShippingZoneRegion> regions, string countryCode, string? postcode)
    {
        var code = (countryCode ?? "").ToUpperInvariant();
        var normalized = NormalizePostcode(postcode);
        var inCountry = regions.Where(r => r.CountryCode == code).ToList();
        var byPrefix = inCountry.Where(r => r.Prefix.Length > 0 && PrefixMatches(r.Prefix, normalized)).OrderByDescending(r => r.Prefix.Length).FirstOrDefault();
        if (byPrefix is not null) return byPrefix.ZoneId;
        var whole = inCountry.FirstOrDefault(r => r.Prefix.Length == 0);
        if (whole is not null) return whole.ZoneId;
        return regions.FirstOrDefault(r => r.CountryCode == Everywhere)?.ZoneId;
    }

    public static async Task<Guid?> ResolveAsync(PiranhaCommerceDbContext db, string countryCode, string? postcode, CancellationToken ct)
    {
        var code = (countryCode ?? "").ToUpperInvariant();
        var regions = await db.Set<ShippingZoneRegion>().AsNoTracking().Where(r => r.CountryCode == code || r.CountryCode == Everywhere).ToListAsync(ct);
        return Resolve(regions, code, postcode);
    }
}

public interface IShippingZoneService
{
    Task<IReadOnlyList<ShippingZoneDto>> ListAsync(CancellationToken ct = default);
    Task<ShippingZoneDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Validation error for a bad region, or a country and postcode start that another zone already holds.</summary>
    Task<ShippingZoneDto> CreateAsync(ShippingZoneInput input, CancellationToken ct = default);

    /// <summary>Replaces the name, position and all the regions. Null when not found.</summary>
    Task<ShippingZoneDto?> UpdateAsync(Guid id, ShippingZoneInput input, CancellationToken ct = default);

    /// <summary>Conflict while a shipping method's rates use the zone: change those first, so the methods offered do not change by surprise. False when not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ShippingZoneService(PiranhaCommerceDbContext db) : IShippingZoneService
{
    public async Task<IReadOnlyList<ShippingZoneDto>> ListAsync(CancellationToken ct = default)
    {
        var zones = await db.Set<ShippingZone>().AsNoTracking().OrderBy(z => z.Position).ThenBy(z => z.Name).ThenBy(z => z.Id).ToListAsync(ct);
        return await ToDtosAsync(zones, ct);
    }

    public async Task<ShippingZoneDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var zone = await db.Set<ShippingZone>().AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, ct);
        return zone is null ? null : (await ToDtosAsync([zone], ct))[0];
    }

    public async Task<ShippingZoneDto> CreateAsync(ShippingZoneInput input, CancellationToken ct = default)
    {
        var rows = await CheckAsync(input, null, ct);
        var zone = new ShippingZone { Name = input.Name.Trim(), Position = input.Position };
        db.Add(zone);
        foreach (var r in rows) db.Add(new ShippingZoneRegion { ZoneId = zone.Id, CountryCode = r.Country, Prefix = r.Prefix });
        await SaveAsync(ct);
        return (await GetAsync(zone.Id, ct))!;
    }

    public async Task<ShippingZoneDto?> UpdateAsync(Guid id, ShippingZoneInput input, CancellationToken ct = default)
    {
        if (!await db.Set<ShippingZone>().AnyAsync(z => z.Id == id, ct)) return null;
        var rows = await CheckAsync(input, id, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        var found = await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                var zone = await db.Set<ShippingZone>().FirstOrDefaultAsync(z => z.Id == id, token);
                if (zone is null) return false;
                zone.Name = input.Name.Trim(); zone.Position = input.Position; zone.UpdatedUtc = DateTimeOffset.UtcNow;
                await db.Set<ShippingZoneRegion>().Where(r => r.ZoneId == id).ExecuteDeleteAsync(token);
                foreach (var r in rows) db.Add(new ShippingZoneRegion { ZoneId = id, CountryCode = r.Country, Prefix = r.Prefix });
                await SaveAsync(token);
                return true;
            },
            async token => await db.Set<ShippingZoneRegion>().AsNoTracking().CountAsync(r => r.ZoneId == id, token) == rows.Count,
            ct);
        return found ? await GetAsync(id, ct) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<ShippingZone>().AnyAsync(z => z.Id == id, ct)) return false;
        if (await db.Set<ShippingRate>().AnyAsync(r => r.ZoneId == id, ct))
            throw new PiranhaCommerceConflictException("Shipping rates use this zone. Change those rates first.");
        try { return await db.DeleteTrackedAsync<ShippingZone>(z => z.Id == id, ct) > 0; }
        catch (DbUpdateException) { throw new PiranhaCommerceConflictException("Shipping rates use this zone. Change those rates first."); } // a rate was added just now
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(ShippingZoneInput.Regions), "A country and postcode start can only be in one zone, and another zone already has one of these.");
        }
    }

    /// <summary>The rows the input stands for, or a validation error: a bad region, a repeat within the zone, or one that another zone holds.</summary>
    private async Task<List<(string Country, string Prefix)>> CheckAsync(ShippingZoneInput input, Guid? zoneId, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        var rows = new List<(string Country, string Prefix)>();
        foreach (var region in input.Regions)
        {
            ModelValidator.ValidateOrThrow(region);
            var country = region.CountryCode.ToUpperInvariant();
            var prefixes = region.PostcodePrefixes.Select(ShippingZoneRules.NormalizePostcode).Where(p => p.Length > 0).Distinct().ToList();
            if (country == ShippingZoneRules.Everywhere && prefixes.Count > 0)
                throw new PiranhaCommerceValidationException(nameof(ShippingZoneInput.Regions), "Everywhere else cannot be limited to postcodes.");
            if (region.PostcodePrefixes.Count > 0 && prefixes.Count == 0)
                throw new PiranhaCommerceValidationException(nameof(ShippingZoneInput.Regions), "A postcode start needs letters or digits.");
            if (prefixes.Count == 0) rows.Add((country, ""));
            else rows.AddRange(prefixes.Select(p => (country, p)));
        }
        var repeat = rows.GroupBy(r => r).FirstOrDefault(g => g.Count() > 1);
        if (repeat is not null) throw new PiranhaCommerceValidationException(nameof(ShippingZoneInput.Regions), $"{Describe(repeat.Key)} is listed twice.");

        var countries = rows.Select(r => r.Country).Distinct().ToList();
        var taken = await db.Set<ShippingZoneRegion>().AsNoTracking().Where(r => countries.Contains(r.CountryCode) && r.ZoneId != zoneId)
            .Select(r => new { r.CountryCode, r.Prefix, r.ZoneId }).ToListAsync(ct);
        var clash = taken.FirstOrDefault(t => rows.Contains((t.CountryCode, t.Prefix)));
        if (clash is not null)
        {
            var other = await db.Set<ShippingZone>().AsNoTracking().Where(z => z.Id == clash.ZoneId).Select(z => z.Name).FirstAsync(ct);
            throw new PiranhaCommerceValidationException(nameof(ShippingZoneInput.Regions), $"{Describe((clash.CountryCode, clash.Prefix))} is already in the zone \"{other}\".");
        }
        return rows;
    }

    private static string Describe((string Country, string Prefix) r) =>
        r.Country == ShippingZoneRules.Everywhere ? "Everywhere else" : r.Prefix.Length == 0 ? r.Country : $"{r.Country} {r.Prefix}";

    private async Task<IReadOnlyList<ShippingZoneDto>> ToDtosAsync(IReadOnlyList<ShippingZone> zones, CancellationToken ct)
    {
        var ids = zones.Select(z => z.Id).ToList();
        var regions = await db.Set<ShippingZoneRegion>().AsNoTracking().Where(r => ids.Contains(r.ZoneId)).ToListAsync(ct);
        var counts = (await db.Set<ShippingRate>().AsNoTracking().Where(r => r.ZoneId != null && ids.Contains(r.ZoneId.Value)).GroupBy(r => r.ZoneId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        return zones.Select(z => new ShippingZoneDto(z.Id, z.Name, z.Position,
            regions.Where(r => r.ZoneId == z.Id).GroupBy(r => r.CountryCode).OrderBy(g => g.Key)
                .Select(g => new ShippingZoneRegionDto(g.Key, g.Where(r => r.Prefix.Length > 0).Select(r => r.Prefix).Order(StringComparer.Ordinal).ToList())).ToList(),
            counts.GetValueOrDefault(z.Id))).ToList();
    }
}

/// <summary>A courier: its name and how to build a link that tracks a parcel with it.</summary>
[Audited]
public class ShippingCarrier : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>A web address with <c>{tracking}</c> where the tracking number goes. Empty when the carrier has no public tracking page.</summary>
    public string? TrackingUrlTemplate { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class ShippingCarrierInput : IValidatableObject
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";

    /// <summary>For example <c>https://www.royalmail.com/track-your-item#/tracking-results/{tracking}</c>.</summary>
    [StringLength(500)] public string? TrackingUrlTemplate { get; set; }
    public bool Active { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(TrackingUrlTemplate)) yield break;
        var template = TrackingUrlTemplate.Trim();
        if (!template.Contains(ShippingCarriers.Placeholder, StringComparison.Ordinal))
            yield return new ValidationResult($"The tracking link must contain {ShippingCarriers.Placeholder} where the tracking number goes.", [nameof(TrackingUrlTemplate)]);
        else if (!Uri.TryCreate(template.Replace(ShippingCarriers.Placeholder, "X"), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            yield return new ValidationResult("The tracking link must be a web address starting with http:// or https://.", [nameof(TrackingUrlTemplate)]);
    }
}

public sealed record ShippingCarrierDto(Guid Id, string Name, string? TrackingUrlTemplate, bool Active);

public static class ShippingCarriers
{
    public const string Placeholder = "{tracking}";

    /// <summary>The tracking link for a number, or null when the carrier has no template or there is no number.</summary>
    public static string? TrackingUrl(string? template, string? trackingNumber) =>
        string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(trackingNumber)
            ? null
            : template.Trim().Replace(Placeholder, Uri.EscapeDataString(trackingNumber.Trim()), StringComparison.Ordinal);
}

public interface IShippingCarrierService
{
    Task<IReadOnlyList<ShippingCarrierDto>> ListAsync(bool activeOnly, CancellationToken ct = default);
    Task<ShippingCarrierDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ShippingCarrierDto> CreateAsync(ShippingCarrierInput input, CancellationToken ct = default);
    Task<ShippingCarrierDto?> UpdateAsync(Guid id, ShippingCarrierInput input, CancellationToken ct = default);

    /// <summary>Parcels already sent keep the carrier name and link they were given, so deleting is always safe.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ShippingCarrierService(PiranhaCommerceDbContext db) : IShippingCarrierService
{
    public async Task<IReadOnlyList<ShippingCarrierDto>> ListAsync(bool activeOnly, CancellationToken ct = default) =>
        await db.Set<ShippingCarrier>().AsNoTracking().Where(c => !activeOnly || c.Active).OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new ShippingCarrierDto(c.Id, c.Name, c.TrackingUrlTemplate, c.Active)).ToListAsync(ct);

    public async Task<ShippingCarrierDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Set<ShippingCarrier>().AsNoTracking().Where(c => c.Id == id).Select(c => new ShippingCarrierDto(c.Id, c.Name, c.TrackingUrlTemplate, c.Active)).FirstOrDefaultAsync(ct);

    public async Task<ShippingCarrierDto> CreateAsync(ShippingCarrierInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var c = new ShippingCarrier { Name = input.Name.Trim() };
        Apply(c, input);
        db.Add(c);
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<ShippingCarrierDto?> UpdateAsync(Guid id, ShippingCarrierInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var c = await db.Set<ShippingCarrier>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        Apply(c, input);
        c.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await db.DeleteTrackedAsync<ShippingCarrier>(c => c.Id == id, ct) > 0;

    private static void Apply(ShippingCarrier c, ShippingCarrierInput i)
    {
        c.Name = i.Name.Trim();
        c.TrackingUrlTemplate = string.IsNullOrWhiteSpace(i.TrackingUrlTemplate) ? null : i.TrackingUrlTemplate.Trim();
        c.Active = i.Active;
    }

    private static ShippingCarrierDto ToDto(ShippingCarrier c) => new(c.Id, c.Name, c.TrackingUrlTemplate, c.Active);
}

internal sealed class ShippingZoneAndCarrierEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var zones = api.MapGroup("/shipping-zones").WithTags("Shipping");
        zones.MapGet("/", async (IShippingZoneService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        zones.MapGet("/{id:guid}", async (Guid id, IShippingZoneService svc, CancellationToken ct) => await svc.GetAsync(id, ct) is { } z ? Results.Ok(z) : Results.NotFound());
        zones.MapPost("/", async (ShippingZoneInput input, IShippingZoneService svc, CancellationToken ct) =>
        {
            var z = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/v1/shipping-zones/{z.Id}", z);
        }).RequireAuthorization(ApiPolicies.Write);
        zones.MapPut("/{id:guid}", async (Guid id, ShippingZoneInput input, IShippingZoneService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } z ? Results.Ok(z) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        zones.MapDelete("/{id:guid}", async (Guid id, IShippingZoneService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);

        var carriers = api.MapGroup("/shipping-carriers").WithTags("Shipping");
        carriers.MapGet("/", async (bool? activeOnly, IShippingCarrierService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(activeOnly ?? false, ct)));
        carriers.MapGet("/{id:guid}", async (Guid id, IShippingCarrierService svc, CancellationToken ct) => await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());
        carriers.MapPost("/", async (ShippingCarrierInput input, IShippingCarrierService svc, CancellationToken ct) =>
        {
            var c = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/v1/shipping-carriers/{c.Id}", c);
        }).RequireAuthorization(ApiPolicies.Write);
        carriers.MapPut("/{id:guid}", async (Guid id, ShippingCarrierInput input, IShippingCarrierService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } c ? Results.Ok(c) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        carriers.MapDelete("/{id:guid}", async (Guid id, IShippingCarrierService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
    }
}
