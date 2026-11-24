using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Catalog;

public interface IStockLocationService
{
    Task<IReadOnlyList<StockLocationDto>> ListAsync(CancellationToken ct = default);
    Task<StockLocationDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>A duplicate code (case-insensitive) is a <see cref="PiranhaCommerceConflictException"/>.</summary>
    [RequiresPermission(Permissions.StockAdjust)]
    Task<StockLocationDto> CreateAsync(StockLocationInput input, CancellationToken ct = default);

    /// <summary>Renames or (de)activates; the code never changes. The default location cannot be deactivated. Null when not found.</summary>
    [RequiresPermission(Permissions.StockAdjust)]
    Task<StockLocationDto?> UpdateAsync(Guid id, StockLocationInput input, CancellationToken ct = default);
}

internal sealed class StockLocationService(PiranhaCommerceDbContext db) : IStockLocationService
{
    public async Task<IReadOnlyList<StockLocationDto>> ListAsync(CancellationToken ct = default)
        => await db.Set<StockLocation>().AsNoTracking().OrderBy(l => l.Code)
            .Select(l => new StockLocationDto(l.Id, l.Code, l.Name, l.Active, l.Id == StockLocations.DefaultId)).ToListAsync(ct);

    public async Task<StockLocationDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Set<StockLocation>().AsNoTracking().Where(l => l.Id == id)
            .Select(l => new StockLocationDto(l.Id, l.Code, l.Name, l.Active, l.Id == StockLocations.DefaultId)).FirstOrDefaultAsync(ct);

    public async Task<StockLocationDto> CreateAsync(StockLocationInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var code = input.Code.Trim().ToUpperInvariant();
        if (await db.Set<StockLocation>().AnyAsync(l => l.Code == code, ct))
            throw new PiranhaCommerceConflictException($"A stock location with code '{code}' already exists.");

        var entity = new StockLocation { Code = code, Name = input.Name.Trim(), Active = input.Active };
        db.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // lost a race on the unique code
            throw new PiranhaCommerceConflictException($"A stock location with code '{code}' already exists.");
        }
        return ToDto(entity);
    }

    public async Task<StockLocationDto?> UpdateAsync(Guid id, StockLocationInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<StockLocation>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        if (id == StockLocations.DefaultId && !input.Active)
            throw new PiranhaCommerceValidationException(nameof(StockLocationInput.Active), "The default location cannot be deactivated.");

        entity.Name = input.Name.Trim();
        entity.Active = input.Active;
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    private static StockLocationDto ToDto(StockLocation l) => new(l.Id, l.Code, l.Name, l.Active, l.Id == StockLocations.DefaultId);
}
