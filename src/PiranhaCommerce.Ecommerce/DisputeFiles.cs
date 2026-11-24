using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>What a document is evidence of. One document per kind per dispute (the bank has one place for each).</summary>
public enum DisputeEvidenceKind
{
    Receipt = 0,
    ShippingDocumentation = 1,
    CustomerCommunication = 2,
    ServiceDocumentation = 3,
    CancellationPolicy = 4,
    RefundPolicy = 5,
    CustomerSignature = 6,
    Other = 7,
}

/// <summary>A document the shop attached as evidence for a dispute (a receipt, a proof of delivery, emails with the buyer). The bytes are in the file store.</summary>
[Audited]
public class DisputeEvidenceFile : Entity
{
    public Guid DisputeId { get; set; }
    public DisputeEvidenceKind Kind { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Length { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset UploadedUtc { get; set; }
}

/// <summary>Settings (section <c>PiranhaCommerce:Ecommerce:DisputeFiles</c>).</summary>
public sealed class DisputeFileOptions
{
    public const string SectionName = "PiranhaCommerce:Ecommerce:DisputeFiles";

    /// <summary>The largest single document. Stripe takes up to 5 MB.</summary>
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>All of a dispute's documents together. Stripe refuses evidence over 4.5 MB in all.</summary>
    public long MaxTotalBytes { get; set; } = 4_500_000;
}

public sealed record DisputeFileDto(Guid Id, Guid DisputeId, DisputeEvidenceKind Kind, string FileName, string ContentType, long Length, DateTimeOffset UploadedUtc, string? Actor);

/// <summary>An opened document. The caller disposes <see cref="Content"/>.</summary>
public sealed record DisputeFileDownload(Stream Content, string ContentType, string FileName);

internal sealed partial class DisputeService
{
    private static string Key(Guid disputeId, Guid fileId) => $"disputes/{disputeId}/{fileId}";

    private static DisputeFileDto ToDto(DisputeEvidenceFile f) => new(f.Id, f.DisputeId, f.Kind, f.FileName, f.ContentType, f.Length, f.UploadedUtc, f.Actor);

    public async Task<IReadOnlyList<DisputeFileDto>?> ListFilesAsync(Guid disputeId, CancellationToken ct = default)
    {
        if (!await db.Set<PaymentDispute>().AnyAsync(d => d.Id == disputeId, ct)) return null;
        return (await db.Set<DisputeEvidenceFile>().AsNoTracking().Where(f => f.DisputeId == disputeId).OrderBy(f => f.Kind).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<DisputeFileDto?> AddFileAsync(Guid disputeId, DisputeEvidenceKind kind, Stream content, string fileName, string? actor, CancellationToken ct = default)
    {
        var d = await db.Set<PaymentDispute>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == disputeId, ct);
        if (d is null) return null;
        if (d.Status != DisputeStatus.Open) throw new PiranhaConflictException("This dispute has already been decided.");
        if (d.EvidenceSubmittedUtc is not null) throw new PiranhaConflictException("Evidence has already been sent to the bank for this dispute and cannot be changed.");
        if (!Enum.IsDefined(kind)) throw new PiranhaValidationException(nameof(kind), "Choose what the document is.");

        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (name.Length == 0) throw new PiranhaValidationException("file", "The file needs a name.");
        if (name.Length > 200) name = name[^200..];

        var existing = await db.Set<DisputeEvidenceFile>().AsNoTracking().Where(f => f.DisputeId == disputeId).Select(f => new { f.Kind, f.Length }).ToListAsync(ct);
        if (existing.Any(f => f.Kind == kind)) throw new PiranhaConflictException($"There is already a document for {kind}. Remove it first.");

        var limits = fileOptions.Value;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limits.MaxBytes) throw new PiranhaValidationException("file", $"A document can be at most {limits.MaxBytes / (1024 * 1024)} MB.");
        }
        if (buffer.Length == 0) throw new PiranhaValidationException("file", "The file is empty.");
        if (existing.Sum(f => f.Length) + buffer.Length > limits.MaxTotalBytes)
            throw new PiranhaValidationException("file", $"The documents for one dispute can be at most {limits.MaxTotalBytes / 1_000_000.0:0.#} MB in all.");

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var contentType = Sniff(bytes) ?? throw new PiranhaValidationException("file", "Documents must be PDF, JPEG or PNG files.");

        var row = new DisputeEvidenceFile { DisputeId = disputeId, Kind = kind, FileName = name, ContentType = contentType, Length = buffer.Length, Actor = actor, UploadedUtc = DateTimeOffset.UtcNow };
        buffer.Position = 0;
        await files.SaveAsync(Key(disputeId, row.Id), buffer, ct);
        try
        {
            db.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await files.DeleteFolderAsync(Key(disputeId, row.Id), ct);
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The dispute changed while the file was being added. Reload and try again.");
        }
        return ToDto(row);
    }

    public async Task<DisputeFileDownload?> OpenFileAsync(Guid disputeId, Guid fileId, CancellationToken ct = default)
    {
        var f = await db.Set<DisputeEvidenceFile>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.DisputeId == disputeId, ct);
        if (f is null) return null;
        var stream = await files.OpenAsync(Key(disputeId, f.Id), ct);
        return stream is null ? null : new DisputeFileDownload(stream, f.ContentType, f.FileName);
    }

    public async Task<bool> DeleteFileAsync(Guid disputeId, Guid fileId, CancellationToken ct = default)
    {
        var d = await db.Set<PaymentDispute>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == disputeId, ct);
        if (d is null) return false;
        if (d.EvidenceSubmittedUtc is not null) throw new PiranhaConflictException("Evidence has already been sent to the bank for this dispute and cannot be changed.");
        var removed = await db.DeleteTrackedAsync<DisputeEvidenceFile>(f => f.Id == fileId && f.DisputeId == disputeId, ct) > 0;
        if (removed) await files.DeleteFolderAsync(Key(disputeId, fileId), ct);
        return removed;
    }

    /// <summary>What the gateway gets for each attached document: a way to open it when it is ready to send it.</summary>
    private async Task<IReadOnlyList<DisputeEvidenceDocument>> DocumentsAsync(Guid disputeId, CancellationToken ct)
    {
        var rows = await db.Set<DisputeEvidenceFile>().AsNoTracking().Where(f => f.DisputeId == disputeId).OrderBy(f => f.Kind).ToListAsync(ct);
        return rows.Select(f => new DisputeEvidenceDocument(f.Kind, f.FileName, f.ContentType, f.Length,
            async token => await files.OpenAsync(Key(disputeId, f.Id), token) ?? throw new GatewayException("A document is missing from the file store: " + f.FileName))).ToList();
    }

    /// <summary>PDF, JPEG or PNG by the file's first bytes, never by its name or the caller's say-so.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return "application/pdf";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        return null;
    }
}
