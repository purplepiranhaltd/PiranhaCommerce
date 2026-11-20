using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Catalog;

/// <summary>A place where stock is held: a warehouse, a shop floor, a back room. One default location always exists.</summary>
public class StockLocation : Entity
{
    /// <summary>Short stable identifier for integrations. Stored upper-case; unique; cannot be changed after creation.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool Active { get; set; } = true;
}

public static class StockLocations
{
    /// <summary>Id of the location created by the framework, used whenever a caller does not name one.</summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");
    public const string DefaultCode = "MAIN";
}

/// <summary>Quantity on hand for a product at one location. Only ever changed together with a <see cref="StockMovement"/>.</summary>
public class StockLevel
{
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }

    /// <summary>decimal(18,4): fractional quantities allow weighed goods (EPOS). Can go negative only when a caller allows it.</summary>
    public decimal Quantity { get; set; }
}

/// <summary>Append-only audit ledger of every change to a product's stock. Never updated or deleted by the framework.</summary>
public class StockMovement : Entity
{
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public decimal Delta { get; set; }

    /// <summary>Quantity at this location immediately after the movement; null for movements recorded before balances were kept.</summary>
    public decimal? Balance { get; set; }
    public required string Reason { get; set; }
    public string? Reference { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class StockLocationInput
{
    [Required, StringLength(30, MinimumLength = 1)]
    [RegularExpression("^[A-Za-z0-9][A-Za-z0-9_-]*$", ErrorMessage = "Use letters, digits, '-' and '_' only.")]
    public string Code { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public bool Active { get; set; } = true;
}

public sealed record StockLocationDto(Guid Id, string Code, string Name, bool Active, bool IsDefault);

public sealed class StockAdjustmentInput : IValidatableObject
{
    public const decimal MaxMagnitude = 1_000_000_000m;

    /// <summary>Positive adds stock, negative removes it. Up to 4 decimal places; cannot be zero.</summary>
    public decimal Delta { get; set; }

    /// <summary>Location to adjust; the default location when omitted.</summary>
    public Guid? LocationId { get; set; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string Reason { get; set; } = "";

    [StringLength(200)]
    public string? Reference { get; set; }

    /// <summary>
    /// Policy, not a data rule: by default an adjustment that would take stock below zero is refused. Sales channels that must
    /// record what physically happened (e.g. EPOS selling the last item the system thought was gone) pass true; the product then
    /// appears in the negative stock report until it is corrected.
    /// </summary>
    public bool AllowNegative { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Delta == 0) yield return new("Delta cannot be zero.", [nameof(Delta)]);
        if (decimal.Round(Delta, 4) != Delta) yield return new("Delta can have at most 4 decimal places.", [nameof(Delta)]);
        if (Math.Abs(Delta) > MaxMagnitude) yield return new($"Delta must be between -{MaxMagnitude:N0} and {MaxMagnitude:N0}.", [nameof(Delta)]);
    }
}

/// <summary>Quantity at one location. <see cref="Quantity"/> on <see cref="StockLevelDto"/> is the total across locations.</summary>
public sealed record StockLocationLevelDto(Guid LocationId, string LocationCode, string LocationName, decimal Quantity);

public sealed record StockLevelDto(Guid ProductId, decimal Quantity, IReadOnlyList<StockLocationLevelDto> Locations);

public sealed record StockMovementDto(
    Guid Id, Guid ProductId, Guid LocationId, decimal Delta, decimal? Balance, string Reason, string? Reference, string? Actor, DateTimeOffset CreatedUtc);

public sealed record StockAdjustmentResult(StockMovementDto Movement, decimal Quantity);

/// <summary>A product that currently has less than nothing at a location: a recount is needed.</summary>
public sealed record NegativeStockDto(
    Guid ProductId, string ProductName, string? Sku, Guid LocationId, string LocationCode, string LocationName, decimal Quantity);

/// <summary>One line of a batch stock change made inside a caller's transaction (see <see cref="IStockService.ApplyInTransactionAsync"/>).</summary>
public sealed record StockChange(Guid ProductId, decimal Delta, string Reason, string? Reference, string? Label = null);
