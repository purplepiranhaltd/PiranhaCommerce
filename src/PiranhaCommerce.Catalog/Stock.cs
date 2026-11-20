using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Catalog;

/// <summary>Current quantity on hand for a product. Only ever changed together with a <see cref="StockMovement"/>.</summary>
public class StockLevel
{
    public Guid ProductId { get; set; }

    /// <summary>decimal(18,4): fractional quantities allow weighed goods (EPOS). Can go negative only when a caller allows it.</summary>
    public decimal Quantity { get; set; }
}

/// <summary>Append-only audit ledger of every change to a product's stock. Never updated or deleted by the framework.</summary>
public class StockMovement : Entity
{
    public Guid ProductId { get; set; }
    public decimal Delta { get; set; }
    public required string Reason { get; set; }
    public string? Reference { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class StockAdjustmentInput : IValidatableObject
{
    public const decimal MaxMagnitude = 1_000_000_000m;

    /// <summary>Positive adds stock, negative removes it. Up to 4 decimal places; cannot be zero.</summary>
    public decimal Delta { get; set; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string Reason { get; set; } = "";

    [StringLength(200)]
    public string? Reference { get; set; }

    /// <summary>
    /// Policy, not a data rule: by default an adjustment that would take stock below zero is refused. Sales channels that must
    /// record what physically happened (e.g. EPOS selling the last item the system thought was gone) pass true.
    /// </summary>
    public bool AllowNegative { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Delta == 0) yield return new("Delta cannot be zero.", [nameof(Delta)]);
        if (decimal.Round(Delta, 4) != Delta) yield return new("Delta can have at most 4 decimal places.", [nameof(Delta)]);
        if (Math.Abs(Delta) > MaxMagnitude) yield return new($"Delta must be between -{MaxMagnitude:N0} and {MaxMagnitude:N0}.", [nameof(Delta)]);
    }
}

public sealed record StockLevelDto(Guid ProductId, decimal Quantity);

public sealed record StockMovementDto(Guid Id, Guid ProductId, decimal Delta, string Reason, string? Reference, string? Actor, DateTimeOffset CreatedUtc);

public sealed record StockAdjustmentResult(StockMovementDto Movement, decimal Quantity);
