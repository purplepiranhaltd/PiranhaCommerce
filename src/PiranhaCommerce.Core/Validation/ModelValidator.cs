using System.ComponentModel.DataAnnotations;

namespace PiranhaCommerce.Core.Validation;

/// <summary>Thrown by services when input is invalid. API maps it to 400 ValidationProblem; admin shows the messages.</summary>
public sealed class PiranhaCommerceValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public PiranhaCommerceValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}

public static class ModelValidator
{
    /// <summary>Validates DataAnnotations on <paramref name="model"/> (including nested via IValidatableObject); throws on failure.</summary>
    public static void ValidateOrThrow(object model)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true))
            return;

        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(m => (Member: m, r.ErrorMessage)))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value.").ToArray());
        throw new PiranhaCommerceValidationException(errors);
    }
}

/// <summary>Thrown when a request is valid but conflicts with current state (e.g. deleting something still in use). Maps to HTTP 409.</summary>
public sealed class PiranhaCommerceConflictException(string message) : Exception(message);

/// <summary>Thrown when the signed-in user is not allowed to do this (for example, acting at a stock location they are not assigned to). Maps to HTTP 403.</summary>
public sealed class PiranhaCommerceForbiddenException(string message) : Exception(message);
