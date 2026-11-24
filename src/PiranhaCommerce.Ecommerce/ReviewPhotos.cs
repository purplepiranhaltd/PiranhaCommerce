using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A picture a shopper attached to their review. It is shown to shoppers only while its review is approved, so staff see every photo before the public does.
/// The file lives in the file store under <c>reviews/{reviewId}/{photoId}</c>; the database holds only this row.
/// </summary>
public class ReviewPhoto : Entity
{
    public Guid ReviewId { get; set; }
    public required string ContentType { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>The smaller sizes made, as comma separated longest sides.</summary>
    public string Sizes { get; set; } = "";
    public string? SizeContentType { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <param name="Url">Where a shop front fetches it (anonymous, cacheable; add <c>?width=</c> for the smallest made size at least that big). It answers 404 until the review is approved: the writer and staff have their own addresses.</param>
public sealed record ReviewPhotoDto(Guid Id, string Url, int? Width, int? Height);

public static class ReviewPhotoUrls
{
    public const string Prefix = "/api/v1/storefront/guest/review-photos/";
}

public interface IReviewPhotoService
{
    /// <summary>
    /// Adds a photo to the customer's own review. Null when they have no such review. A validation error for a file that is too big, not an image, or one too many.
    /// With review approval on, the review goes back to waiting for staff, because there is something new to read.
    /// </summary>
    Task<ReviewPhotoDto?> AddAsync(Guid customerId, Guid reviewId, Stream content, string? fileName, CancellationToken ct = default);

    /// <summary>Takes a photo off the customer's own review (the review goes back to waiting when approval is on). False when it is not theirs.</summary>
    Task<bool> DeleteOwnAsync(Guid customerId, Guid reviewId, Guid photoId, CancellationToken ct = default);

    /// <summary>Staff take a photo off any review, without touching the review's state. False when there is no such photo.</summary>
    [RequiresPermission(Permissions.ReviewsModerate)]
    Task<bool> DeleteAsync(Guid reviewId, Guid photoId, CancellationToken ct = default);

    /// <summary>The photo for a shop page: only when its review is approved and its product is on sale.</summary>
    Task<ImageFile?> OpenPublicAsync(Guid photoId, int? width, CancellationToken ct = default);

    /// <summary>The photo for the review's own writer, whatever the review's state. Null when it is not theirs.</summary>
    Task<ImageFile?> OpenOwnAsync(Guid customerId, Guid photoId, int? width, CancellationToken ct = default);

    /// <summary>The photo for staff, whatever the review's state.</summary>
    Task<ImageFile?> OpenAnyAsync(Guid photoId, int? width, CancellationToken ct = default);
}

internal sealed class ReviewPhotoService(PiranhaCommerceDbContext db, IFileStore store, IOptions<ImageOptions> imageOptions, IOptions<EcommerceOptions> options, IImageProcessor? processor = null) : IReviewPhotoService
{
    /// <summary>The longest side kept of what a shopper uploads, when an image processor can re-make it.</summary>
    private const int KeptSide = 2000;

    private static string Folder(Guid reviewId, Guid photoId) => $"reviews/{reviewId}/{photoId}";

    public async Task<ReviewPhotoDto?> AddAsync(Guid customerId, Guid reviewId, Stream content, string? fileName, CancellationToken ct = default)
    {
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId && r.CustomerId == customerId, ct);
        if (review is null) return null;
        var max = options.Value.MaxReviewPhotos;
        if (max <= 0) throw new PiranhaCommerceValidationException("file", "This shop does not take photos with reviews.");
        if (await db.Set<ReviewPhoto>().CountAsync(p => p.ReviewId == reviewId, ct) >= max) throw new PiranhaCommerceValidationException("file", $"A review can have at most {max} photos.");

        var limit = imageOptions.Value.MaxBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) throw new PiranhaCommerceValidationException("file", $"A photo can be at most {limit / (1024 * 1024d):0.#} MB.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var type = ImageTypes.Sniff(bytes) ?? throw new PiranhaCommerceValidationException("file", "Upload a JPEG, PNG, WebP or GIF photo.");

        int? width = null, height = null;
        var sizes = new List<(int Size, ProcessedImage Image)>();
        if (processor is not null)
        {
            var info = await processor.ReadSizeAsync(new MemoryStream(bytes), ct) ?? throw new PiranhaCommerceValidationException("file", "That file is not a photo that can be read.");
            // Re-made rather than kept as sent: it drops what a phone writes into a photo (often where it was taken) and turns it upright.
            var kept = await processor.ResizeAsync(new MemoryStream(bytes), KeptSide, KeptSide, ct) ?? throw new PiranhaCommerceValidationException("file", "That file is not a photo that can be read.");
            bytes = kept.Bytes; type = kept.ContentType; width = kept.Width; height = kept.Height;
            foreach (var s in imageOptions.Value.SizeList().Where(s => s < Math.Max(kept.Width, kept.Height)))
                if (await processor.ResizeAsync(new MemoryStream(bytes), s, s, ct) is { } made) sizes.Add((s, made));
        }

        var photo = new ReviewPhoto
        {
            ReviewId = reviewId, ContentType = type, FileName = CleanName(fileName), SizeBytes = bytes.Length, Width = width, Height = height,
            Sizes = string.Join(',', sizes.Select(s => s.Size)), SizeContentType = sizes.Count > 0 ? sizes[0].Image.ContentType : null,
        };
        var folder = Folder(reviewId, photo.Id);
        try
        {
            await store.SaveAsync(folder + "/original", new MemoryStream(bytes), ct);
            foreach (var (size, made) in sizes) await store.SaveAsync($"{folder}/w{size}", new MemoryStream(made.Bytes), ct);
            db.Add(photo);
            BackToWaiting(review);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            try { await store.DeleteFolderAsync(folder, CancellationToken.None); } catch { /* the original failure is the one to report */ }
            throw;
        }
        return ToDto(photo);
    }

    private void BackToWaiting(ProductReview review)
    {
        if (!options.Value.ReviewsNeedApproval) return;
        review.Status = ReviewStatus.Pending; review.ModeratedUtc = null; review.ModeratedBy = null; review.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public async Task<bool> DeleteOwnAsync(Guid customerId, Guid reviewId, Guid photoId, CancellationToken ct = default)
    {
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId && r.CustomerId == customerId, ct);
        if (review is null || !await RemoveAsync(reviewId, photoId, ct)) return false;
        // Taking a photo off needs no second look, so the review keeps its state.
        return true;
    }

    public Task<bool> DeleteAsync(Guid reviewId, Guid photoId, CancellationToken ct = default) => RemoveAsync(reviewId, photoId, ct);

    private async Task<bool> RemoveAsync(Guid reviewId, Guid photoId, CancellationToken ct)
    {
        var removed = await db.Set<ReviewPhoto>().Where(p => p.Id == photoId && p.ReviewId == reviewId).ExecuteDeleteAsync(ct) > 0;
        if (removed) await store.DeleteFolderAsync(Folder(reviewId, photoId), ct); // the row goes first: a failure leaves a stray file, never a photo that points at nothing
        return removed;
    }

    public Task<ImageFile?> OpenPublicAsync(Guid photoId, int? width, CancellationToken ct = default) =>
        OpenAsync(db.Set<ReviewPhoto>().AsNoTracking().Where(p => p.Id == photoId && db.Set<ProductReview>().Any(r => r.Id == p.ReviewId && r.Status == ReviewStatus.Approved
            && db.Set<ProductListing>().Any(l => l.ProductId == r.ProductId && l.Published))), width, ct);

    public Task<ImageFile?> OpenOwnAsync(Guid customerId, Guid photoId, int? width, CancellationToken ct = default) =>
        OpenAsync(db.Set<ReviewPhoto>().AsNoTracking().Where(p => p.Id == photoId && db.Set<ProductReview>().Any(r => r.Id == p.ReviewId && r.CustomerId == customerId)), width, ct);

    public Task<ImageFile?> OpenAnyAsync(Guid photoId, int? width, CancellationToken ct = default) =>
        OpenAsync(db.Set<ReviewPhoto>().AsNoTracking().Where(p => p.Id == photoId), width, ct);

    private async Task<ImageFile?> OpenAsync(IQueryable<ReviewPhoto> query, int? width, CancellationToken ct)
    {
        var photo = await query.FirstOrDefaultAsync(ct);
        if (photo is null) return null;
        var made = photo.Sizes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToList();
        var chosen = width is { } w ? made.Where(s => s >= w).Select(s => (int?)s).FirstOrDefault() : null;
        var stream = await store.OpenAsync(Folder(photo.ReviewId, photo.Id) + (chosen is { } c ? $"/w{c}" : "/original"), ct);
        return stream is null ? null : new ImageFile(stream, chosen is null ? photo.ContentType : photo.SizeContentType ?? photo.ContentType, $"\"{photo.Id:N}-{chosen?.ToString() ?? "o"}\"");
    }

    public static ReviewPhotoDto ToDto(ReviewPhoto p) => new(p.Id, ReviewPhotoUrls.Prefix + p.Id, p.Width, p.Height);

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = new string(Path.GetFileName(name.Trim()).Where(c => !char.IsControl(c)).ToArray());
        return clean.Length == 0 ? null : clean.Length > 200 ? clean[..200] : clean;
    }
}

internal sealed class ReviewPhotoEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // Staff see any photo, so they can look before approving; they can take one off without rejecting the whole review.
        var g = api.MapGroup("/reviews/{reviewId:guid}/photos").WithTags("Reviews").RequirePermission(Permissions.CustomersView);
        g.MapGet("/{photoId:guid}", async (Guid reviewId, Guid photoId, int? width, HttpContext http, IReviewPhotoService svc, CancellationToken ct) =>
            await Send(await svc.OpenAnyAsync(photoId, width, ct), http, "private, no-store"));
        g.MapDelete("/{photoId:guid}", async (Guid reviewId, Guid photoId, IReviewPhotoService svc, CancellationToken ct) =>
            await svc.DeleteAsync(reviewId, photoId, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.ReviewsModerate);
    }

    internal static async Task<IResult> Send(ImageFile? file, HttpContext http, string cacheControl)
    {
        if (file is null) return Results.NotFound();
        http.Response.Headers.ETag = file.ETag;
        http.Response.Headers.CacheControl = cacheControl;
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (http.Request.Headers.IfNoneMatch.Contains(file.ETag))
        {
            await file.Content.DisposeAsync();
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Stream(file.Content, file.ContentType);
    }
}

internal sealed class ReviewPhotoStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // A photo is only reachable by its address while its review is approved, unlike a product picture: it is a shopper's upload that staff have not yet read.
        storefront.MapGet("/guest/review-photos/{photoId:guid}", async (Guid photoId, int? width, HttpContext http, IReviewPhotoService svc, CancellationToken ct) =>
                await ReviewPhotoEndpoints.Send(await svc.OpenPublicAsync(photoId, width, ct), http, "public, max-age=3600"))
            .AllowAnonymous().WithTags("Storefront reviews");

        var mine = storefront.MapGroup("/reviews/{reviewId:guid}/photos").WithTags("Storefront reviews").AddEndpointFilter<ActiveCustomerFilter>();
        mine.MapPost("/", async (Guid reviewId, IFormFile file, ClaimsPrincipal user, IReviewPhotoService svc, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await svc.AddAsync(user.CustomerId()!.Value, reviewId, stream, file.FileName, ct) is { } p
                ? Results.Created($"/api/v1/storefront/reviews/{reviewId}/photos/{p.Id}", p) : Results.NotFound();
        }).DisableAntiforgery();
        mine.MapGet("/{photoId:guid}", async (Guid reviewId, Guid photoId, int? width, HttpContext http, ClaimsPrincipal user, IReviewPhotoService svc, CancellationToken ct) =>
            await ReviewPhotoEndpoints.Send(await svc.OpenOwnAsync(user.CustomerId()!.Value, photoId, width, ct), http, "private, no-store"));
        mine.MapDelete("/{photoId:guid}", async (Guid reviewId, Guid photoId, ClaimsPrincipal user, IReviewPhotoService svc, CancellationToken ct) =>
            await svc.DeleteOwnAsync(user.CustomerId()!.Value, reviewId, photoId, ct) ? Results.NoContent() : Results.NotFound());
    }
}
