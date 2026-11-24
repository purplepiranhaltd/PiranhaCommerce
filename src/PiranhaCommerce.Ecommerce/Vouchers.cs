using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// Stored value a customer pays with. It is a way of PAYING, not a discount: it never changes the price or the tax, only how much of
/// the total is still owed. The code is worth money to whoever holds it, so only a hash of it is stored: the full code is shown once,
/// when the voucher is issued.
/// </summary>
[Audited]
public class GiftVoucher : AuditableEntity
{
    /// <summary>SHA-256 (hex) of the code with dashes and spaces removed, upper-case.</summary>
    public required string CodeHash { get; set; }

    /// <summary>The last four characters, so staff and customers can tell vouchers apart without the secret.</summary>
    public required string CodeLast4 { get; set; }

    public decimal InitialValue { get; set; }

    /// <summary>What is left. Always equals the sum of the voucher's ledger entries.</summary>
    public decimal Balance { get; set; }

    public required string Currency { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool Active { get; set; } = true;
    [AuditMask] public string? Note { get; set; }
}

public static class VoucherEntryKinds
{
    public const string Issued = "Issued";
    public const string Redeemed = "Redeemed";
    public const string Returned = "Returned";
    public const string Adjusted = "Adjusted";
}

/// <summary>Append-only ledger: every change to a voucher's balance is one row (positive adds, negative spends).</summary>
public class VoucherEntry : Entity
{
    public Guid VoucherId { get; set; }
    public decimal Amount { get; set; }
    public required string Kind { get; set; }
    public Guid? OrderId { get; set; }
    public string? Reference { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class VoucherIssueInput
{
    [Range(0.01, 1_000_000)] public decimal Value { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

public sealed class VoucherUpdateInput
{
    public bool Active { get; set; } = true;
    public DateTimeOffset? ExpiresUtc { get; set; }
    [StringLength(200)] public string? Note { get; set; }
}

public sealed class VoucherAdjustInput
{
    /// <summary>Positive adds value, negative removes it. The balance cannot go below zero.</summary>
    [Range(-1_000_000, 1_000_000)] public decimal Amount { get; set; }
    [Required, StringLength(200)] public string Reason { get; set; } = "";
}

public sealed record VoucherDto(
    Guid Id, string CodeLast4, decimal InitialValue, decimal Balance, string Currency, DateTimeOffset? ExpiresUtc, bool Active, string? Note, DateTimeOffset CreatedUtc);

public sealed record VoucherEntryDto(Guid Id, decimal Amount, string Kind, Guid? OrderId, string? Reference, string? Actor, DateTimeOffset CreatedUtc);

public sealed record VoucherDetailDto(VoucherDto Voucher, IReadOnlyList<VoucherEntryDto> Entries);

/// <param name="Code">The full code. It is not stored and cannot be shown again.</param>
public sealed record VoucherIssuedDto(VoucherDto Voucher, string Code);

public static class VoucherRules
{
    /// <summary>One answer for every kind of failure, so a guessed code reveals nothing.</summary>
    public const string NotValid = "That voucher is not valid.";

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols, no 0/O/1/I
    private const int CodeLength = 16; // 16 x 5 bits = 80 bits

    /// <summary>XXXX-XXXX-XXXX-XXXX from a cryptographic random source.</summary>
    public static string NewCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    public static string Normalize(string code) => new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    /// <summary>Why the voucher cannot be spent right now, or null when it can.</summary>
    public static string? Problem(GiftVoucher? v, string currency, DateTimeOffset now)
    {
        if (v is null || !v.Active || v.Balance <= 0) return NotValid;
        if (v.ExpiresUtc is { } end && end <= now) return NotValid;
        if (!string.Equals(v.Currency, currency, StringComparison.OrdinalIgnoreCase)) return NotValid;
        return null;
    }
}

public interface IVoucherService
{
    Task<PagedResult<VoucherDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default);
    Task<VoucherDetailDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Finds a voucher from its code (a till or counter lookup). Null when there is none.</summary>
    Task<VoucherDto?> FindByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Creates a voucher and returns its code, the only time the code is ever visible.</summary>
    Task<VoucherIssuedDto> IssueAsync(VoucherIssueInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Switches a voucher off or on, changes its expiry or note. The balance only changes through <see cref="AdjustAsync"/>. Null when not found.</summary>
    Task<VoucherDto?> UpdateAsync(Guid id, VoucherUpdateInput input, CancellationToken ct = default);

    /// <summary>Adds or removes value, with a reason, in the ledger. Conflict when it would take the balance below zero. Null when not found.</summary>
    Task<VoucherDto?> AdjustAsync(Guid id, VoucherAdjustInput input, string? actor = null, CancellationToken ct = default);
}

internal sealed class VoucherService(PiranhaCommerceDbContext db, IOptions<EcommerceOptions> options) : IVoucherService
{
    public async Task<PagedResult<VoucherDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<GiftVoucher>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(v => v.CodeLast4 == s.ToUpper() || (v.Note != null && v.Note.ToLower().Contains(s.ToLower())));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(v => v.CreatedUtc).ThenBy(v => v.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<VoucherDto>(items.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<VoucherDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var v = await db.Set<GiftVoucher>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        var entries = await db.Set<VoucherEntry>().AsNoTracking().Where(e => e.VoucherId == id).OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id)
            .Select(e => new VoucherEntryDto(e.Id, e.Amount, e.Kind, e.OrderId, e.Reference, e.Actor, e.CreatedUtc)).ToListAsync(ct);
        return new VoucherDetailDto(ToDto(v), entries);
    }

    public async Task<VoucherDto?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var hash = VoucherRules.Hash(code);
        var v = await db.Set<GiftVoucher>().AsNoTracking().FirstOrDefaultAsync(x => x.CodeHash == hash, ct);
        return v is null ? null : ToDto(v);
    }

    public async Task<VoucherIssuedDto> IssueAsync(VoucherIssueInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.ExpiresUtc is { } end && end <= DateTimeOffset.UtcNow)
            throw new PiranhaCommerceValidationException(nameof(VoucherIssueInput.ExpiresUtc), "The expiry must be in the future.");

        var code = VoucherRules.NewCode();
        var now = DateTimeOffset.UtcNow;
        var voucher = new GiftVoucher
        {
            CodeHash = VoucherRules.Hash(code), CodeLast4 = VoucherRules.Normalize(code)[^4..],
            InitialValue = input.Value, Balance = input.Value, Currency = options.Value.Currency.ToUpperInvariant(),
            ExpiresUtc = input.ExpiresUtc, Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
        };
        db.Add(voucher);
        db.Add(new VoucherEntry { VoucherId = voucher.Id, Amount = input.Value, Kind = VoucherEntryKinds.Issued, Actor = actor, CreatedUtc = now });
        await db.SaveChangesAsync(ct); // voucher and its first ledger row, together
        return new VoucherIssuedDto(ToDto(voucher), code);
    }

    public async Task<VoucherDto?> UpdateAsync(Guid id, VoucherUpdateInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var v = await db.Set<GiftVoucher>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return null;
        v.Active = input.Active;
        v.ExpiresUtc = input.ExpiresUtc;
        v.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        v.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(v);
    }

    public async Task<VoucherDto?> AdjustAsync(Guid id, VoucherAdjustInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.Amount == 0) throw new PiranhaCommerceValidationException(nameof(VoucherAdjustInput.Amount), "Enter an amount other than zero.");
        if (!await db.Set<GiftVoucher>().AnyAsync(v => v.Id == id, ct)) return null;

        var strategy = db.Database.CreateExecutionStrategy();
        var entryId = Guid.NewGuid();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                // One conditional statement: the balance can never go negative, even with simultaneous spends and adjustments.
                var changed = await db.Set<GiftVoucher>().Where(v => v.Id == id && v.Balance + input.Amount >= 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Balance, v => v.Balance + input.Amount).SetProperty(v => v.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), token);
                if (changed == 0) throw new PiranhaCommerceConflictException("That would take the voucher balance below zero.");
                db.Add(new VoucherEntry { Id = entryId, VoucherId = id, Amount = input.Amount, Kind = VoucherEntryKinds.Adjusted, Reference = input.Reason.Trim(), Actor = actor, CreatedUtc = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(token);
                return true;
            },
            async token => await db.Set<VoucherEntry>().AsNoTracking().AnyAsync(e => e.Id == entryId, token),
            ct);

        var v = await db.Set<GiftVoucher>().AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return ToDto(v);
    }

    private static VoucherDto ToDto(GiftVoucher v) =>
        new(v.Id, v.CodeLast4, v.InitialValue, v.Balance, v.Currency, v.ExpiresUtc, v.Active, v.Note, v.CreatedUtc);
}
