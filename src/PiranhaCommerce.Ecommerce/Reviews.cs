using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Ecommerce;

public enum ReviewStatus { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>
/// A signed-in customer's rating and comment on a product. Reviews belong to the parent product (not to one variant). Only approved reviews are shown to shoppers.
/// Text is stored exactly as written, as plain text: whoever shows it must escape it.
/// </summary>
[Audited]
public class ProductReview : AuditableEntity
{
    /// <summary>The product reviewed: always the parent when the product has variants.</summary>
    public Guid ProductId { get; set; }
    public Guid CustomerId { get; set; }
    [AuditMask] public required string DisplayName { get; set; }
    public int Rating { get; set; }
    public string? Title { get; set; }
    [AuditMask] public string? Body { get; set; }
    public ReviewStatus Status { get; set; }

    /// <summary>Whether the reviewer had bought the product (or one of its variants) in an order that was not cancelled, when they wrote it.</summary>
    public bool VerifiedPurchase { get; set; }
    public DateTimeOffset? ModeratedUtc { get; set; }
    public string? ModeratedBy { get; set; }

    /// <summary>The shop's answer to the review: one per review, plain text. Shoppers see it with the review once the review is approved.</summary>
    public string? Reply { get; set; }
    public DateTimeOffset? RepliedUtc { get; set; }
    public string? RepliedBy { get; set; }
}

/// <summary>One shopper's "this review was helpful". A shopper has at most one per review, and none on their own.</summary>
public class ReviewHelpfulVote
{
    public Guid ReviewId { get; set; }
    public Guid CustomerId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class ReviewInput
{
    /// <summary>The product, or one of its variants (the review is kept against the parent).</summary>
    public Guid ProductId { get; set; }

    [Range(1, 5)] public int Rating { get; set; }
    [StringLength(100)] public string? Title { get; set; }
    [StringLength(4000)] public string? Body { get; set; }

    /// <summary>The name shown with the review. Left empty it is the first name and the first letter of the last name, or "A customer". Never the email address.</summary>
    [StringLength(50)] public string? DisplayName { get; set; }
}

public sealed class ReviewUpdateInput
{
    [Range(1, 5)] public int Rating { get; set; }
    [StringLength(100)] public string? Title { get; set; }
    [StringLength(4000)] public string? Body { get; set; }
    [StringLength(50)] public string? DisplayName { get; set; }
}

public sealed class ReviewStatusInput
{
    public ReviewStatus Status { get; set; }
}

public sealed class ReviewReplyInput
{
    /// <summary>The shop's answer, as plain text. Sending another replaces the first.</summary>
    [Required, StringLength(2000, MinimumLength = 1)]
    public string Body { get; set; } = "";
}

/// <summary>What shoppers see: no email, no customer id.</summary>
public sealed record PublicReviewDto(Guid Id, string DisplayName, int Rating, string? Title, string? Body, bool VerifiedPurchase, DateTimeOffset CreatedUtc, string? Reply = null, DateTimeOffset? RepliedUtc = null, int HelpfulCount = 0, bool VotedByMe = false, IReadOnlyList<ReviewPhotoDto>? Photos = null);

/// <param name="Distribution">How many reviews gave 1, 2, 3, 4 and 5 stars (five numbers, in that order).</param>
public sealed record ReviewSummaryDto(Guid ProductId, int Count, decimal Average, IReadOnlyList<int> Distribution);

/// <param name="VotedByMe">Whether the signed-in shopper asking has marked it helpful (always false for anyone else).</param>
public sealed record ReviewHelpfulDto(Guid ReviewId, int HelpfulCount, bool VotedByMe);

public sealed record PublicReviewsDto(ReviewSummaryDto Summary, PagedResult<PublicReviewDto> Reviews);

/// <summary>A review as its writer and staff see it.</summary>
public sealed record ReviewDto(
    Guid Id, Guid ProductId, string? ProductName, Guid CustomerId, string DisplayName, int Rating, string? Title, string? Body, ReviewStatus Status,
    bool VerifiedPurchase, DateTimeOffset CreatedUtc, DateTimeOffset? ModeratedUtc, string? ModeratedBy, string? Reply = null, DateTimeOffset? RepliedUtc = null, string? RepliedBy = null, IReadOnlyList<ReviewPhotoDto>? Photos = null);

public interface IReviewService
{
    /// <summary>Approved reviews and the summary. Null when the product does not exist or is not published.</summary>
    Task<PublicReviewsDto?> ListPublicAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>The same, with <c>VotedByMe</c> filled in for this signed-in shopper.</summary>
    Task<PublicReviewsDto?> ListPublicAsync(Guid productId, int? page, int? pageSize, Guid? customerId, CancellationToken ct = default);

    /// <summary>
    /// Marks a review helpful, or takes the mark back. Doing either twice changes nothing and still succeeds. Null when the review is not one shoppers can see
    /// (it does not exist, is not approved, or its product is not on sale). A validation error for the shopper's own review.
    /// </summary>
    Task<ReviewHelpfulDto?> SetHelpfulAsync(Guid customerId, Guid reviewId, bool helpful, CancellationToken ct = default);

    Task<ReviewSummaryDto?> SummaryAsync(Guid productId, CancellationToken ct = default);

    /// <summary>The number of approved reviews and their average for each of the given products (parent products: that is where reviews are kept). A product with none is left out.</summary>
    Task<IReadOnlyDictionary<Guid, (int Count, decimal Average)>> SummariesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Adds the customer's review. Conflict when they have already reviewed this product; validation error when the product is not published.
    /// Pending until staff approve it, unless the shop has switched approval off.
    /// </summary>
    Task<ReviewDto> CreateAsync(Guid customerId, ReviewInput input, CancellationToken ct = default);

    /// <summary>Changes the customer's own review (it goes back to pending when approval is on). Null when they have none on that product.</summary>
    Task<ReviewDto?> UpdateOwnAsync(Guid customerId, Guid reviewId, ReviewUpdateInput input, CancellationToken ct = default);

    Task<bool> DeleteOwnAsync(Guid customerId, Guid reviewId, CancellationToken ct = default);
    Task<IReadOnlyList<ReviewDto>> ListOwnAsync(Guid customerId, CancellationToken ct = default);

    Task<PagedResult<ReviewDto>> ListAsync(ReviewStatus? status, Guid? productId, int? page, int? pageSize, CancellationToken ct = default);
    [RequiresPermission(Permissions.ReviewsModerate)]
    Task<ReviewDto?> SetStatusAsync(Guid reviewId, ReviewStatus status, string? actor, CancellationToken ct = default);
    [RequiresPermission(Permissions.ReviewsModerate)]
    Task<bool> DeleteAsync(Guid reviewId, CancellationToken ct = default);

    /// <summary>Sets (or replaces) the shop's reply to a review, whatever its status; shoppers see it once the review is approved. Null when there is no such review. A validation error for an empty reply.</summary>
    [RequiresPermission(Permissions.ReviewsModerate)]
    Task<ReviewDto?> SetReplyAsync(Guid reviewId, string body, string? actor, CancellationToken ct = default);

    /// <summary>Removes the reply. Null when there is no such review; removing a reply that is not there is not an error.</summary>
    [RequiresPermission(Permissions.ReviewsModerate)]
    Task<ReviewDto?> DeleteReplyAsync(Guid reviewId, CancellationToken ct = default);
}

internal sealed class ReviewService(PiranhaCommerceDbContext db, IOptions<EcommerceOptions> options, IEmailSender email, IFileStore store, ILogger<ReviewService> logger) : IReviewService
{
    /// <summary>The product a review is kept against: the parent of a variant, else the product itself. Null when there is no such product.</summary>
    private async Task<Guid?> ReviewedProductAsync(Guid productId, CancellationToken ct)
    {
        var parent = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        var id = parent ?? productId;
        return await db.Set<Product>().AnyAsync(p => p.Id == id, ct) ? id : null;
    }

    private Task<bool> IsPublishedAsync(Guid productId, CancellationToken ct) =>
        db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == productId && l.Published, ct);

    public async Task<ReviewSummaryDto?> SummaryAsync(Guid productId, CancellationToken ct = default)
    {
        if (await ReviewedProductAsync(productId, ct) is not { } id || !await IsPublishedAsync(id, ct)) return null;
        return await SummaryOfAsync(id, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, (int Count, decimal Average)>> SummariesAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, (int, decimal)>();
        var rows = await db.Set<ProductReview>().AsNoTracking().Where(r => productIds.Contains(r.ProductId) && r.Status == ReviewStatus.Approved)
            .GroupBy(r => r.ProductId).Select(g => new { Id = g.Key, N = g.Count(), Sum = g.Sum(r => r.Rating) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.N, Math.Round((decimal)r.Sum / r.N, 2)));
    }

    private async Task<ReviewSummaryDto> SummaryOfAsync(Guid id, CancellationToken ct)
    {
        var counts = await db.Set<ProductReview>().AsNoTracking().Where(r => r.ProductId == id && r.Status == ReviewStatus.Approved)
            .GroupBy(r => r.Rating).Select(g => new { Rating = g.Key, N = g.Count() }).ToListAsync(ct);
        var distribution = Enumerable.Range(1, 5).Select(star => counts.FirstOrDefault(c => c.Rating == star)?.N ?? 0).ToList();
        var count = distribution.Sum();
        var average = count == 0 ? 0m : Math.Round(distribution.Select((n, i) => (i + 1) * (decimal)n).Sum() / count, 2);
        return new ReviewSummaryDto(id, count, average, distribution);
    }

    public Task<PublicReviewsDto?> ListPublicAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default) => ListPublicAsync(productId, page, pageSize, null, ct);

    public async Task<PublicReviewsDto?> ListPublicAsync(Guid productId, int? page, int? pageSize, Guid? customerId, CancellationToken ct = default)
    {
        if (await ReviewedProductAsync(productId, ct) is not { } id || !await IsPublishedAsync(id, ct)) return null;
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ProductReview>().AsNoTracking().Where(r => r.ProductId == id && r.Status == ReviewStatus.Approved);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id).Skip((p - 1) * size).Take(size)
            .Select(r => new PublicReviewDto(r.Id, r.DisplayName, r.Rating, r.Title, r.Body, r.VerifiedPurchase, r.CreatedUtc, r.Reply, r.RepliedUtc)).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var counts = await db.Set<ReviewHelpfulVote>().AsNoTracking().Where(v => ids.Contains(v.ReviewId)).GroupBy(v => v.ReviewId)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var mine = customerId is { } me
            ? (await db.Set<ReviewHelpfulVote>().AsNoTracking().Where(v => v.CustomerId == me && ids.Contains(v.ReviewId)).Select(v => v.ReviewId).ToListAsync(ct)).ToHashSet()
            : [];
        var photos = await PhotosAsync(ids, ct);
        items = items.Select(i => i with { HelpfulCount = counts.GetValueOrDefault(i.Id), VotedByMe = mine.Contains(i.Id), Photos = photos.GetValueOrDefault(i.Id, []) }).ToList();
        return new PublicReviewsDto(await SummaryOfAsync(id, ct), new PagedResult<PublicReviewDto>(items, p, size, total));
    }

    public async Task<ReviewHelpfulDto?> SetHelpfulAsync(Guid customerId, Guid reviewId, bool helpful, CancellationToken ct = default)
    {
        var review = await db.Set<ProductReview>().AsNoTracking().Where(r => r.Id == reviewId && r.Status == ReviewStatus.Approved
                && db.Set<ProductListing>().Any(l => l.ProductId == r.ProductId && l.Published))
            .Select(r => new { r.Id, r.CustomerId }).FirstOrDefaultAsync(ct);
        if (review is null) return null;
        if (review.CustomerId == customerId) throw new PiranhaCommerceValidationException("reviewId", "You cannot mark your own review as helpful.");

        var has = await db.Set<ReviewHelpfulVote>().AnyAsync(v => v.ReviewId == reviewId && v.CustomerId == customerId, ct);
        if (helpful && !has)
        {
            var vote = new ReviewHelpfulVote { ReviewId = reviewId, CustomerId = customerId, CreatedUtc = DateTimeOffset.UtcNow };
            db.Add(vote);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.Entry(vote).State = EntityState.Detached; } // two taps at once: the key decided, and the vote is there either way
        }
        else if (!helpful && has)
        {
            await db.Set<ReviewHelpfulVote>().Where(v => v.ReviewId == reviewId && v.CustomerId == customerId).ExecuteDeleteAsync(ct);
        }
        var count = await db.Set<ReviewHelpfulVote>().CountAsync(v => v.ReviewId == reviewId, ct);
        return new ReviewHelpfulDto(reviewId, count, await db.Set<ReviewHelpfulVote>().AnyAsync(v => v.ReviewId == reviewId && v.CustomerId == customerId, ct));
    }

    public async Task<ReviewDto> CreateAsync(Guid customerId, ReviewInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var id = await ReviewedProductAsync(input.ProductId, ct);
        if (id is null || !await IsPublishedAsync(id.Value, ct))
            throw new PiranhaCommerceValidationException(nameof(ReviewInput.ProductId), "That product cannot be reviewed.");
        if (await db.Set<ProductReview>().AnyAsync(r => r.ProductId == id && r.CustomerId == customerId, ct))
            throw new PiranhaCommerceConflictException("You have already reviewed this product. Change your review instead.");

        var review = new ProductReview
        {
            ProductId = id.Value, CustomerId = customerId, Rating = input.Rating, Title = Clean(input.Title), Body = Clean(input.Body),
            DisplayName = await NameAsync(customerId, input.DisplayName, ct),
            Status = options.Value.ReviewsNeedApproval ? ReviewStatus.Pending : ReviewStatus.Approved,
            VerifiedPurchase = await HasBoughtAsync(customerId, id.Value, ct),
        };
        db.Add(review);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // a second review sent at the same moment: the unique index decides
            throw new PiranhaCommerceConflictException("You have already reviewed this product. Change your review instead.");
        }
        return await ToDtoAsync(review.Id, ct) ?? throw new InvalidOperationException("The review just saved could not be read.");
    }

    public async Task<ReviewDto?> UpdateOwnAsync(Guid customerId, Guid reviewId, ReviewUpdateInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId && r.CustomerId == customerId, ct);
        if (review is null) return null;
        review.Rating = input.Rating; review.Title = Clean(input.Title); review.Body = Clean(input.Body);
        if (!string.IsNullOrWhiteSpace(input.DisplayName)) review.DisplayName = input.DisplayName.Trim();
        review.Status = options.Value.ReviewsNeedApproval ? ReviewStatus.Pending : ReviewStatus.Approved;
        review.ModeratedUtc = null; review.ModeratedBy = null;
        review.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(review.Id, ct);
    }

    public async Task<bool> DeleteOwnAsync(Guid customerId, Guid reviewId, CancellationToken ct = default)
    {
        var hadPhotos = await db.Set<ReviewPhoto>().AnyAsync(p => p.ReviewId == reviewId && db.Set<ProductReview>().Any(r => r.Id == reviewId && r.CustomerId == customerId), ct);
        var removed = await db.DeleteTrackedAsync<ProductReview>(r => r.Id == reviewId && r.CustomerId == customerId, ct) > 0;
        if (removed && hadPhotos) await store.DeleteFolderAsync($"reviews/{reviewId}", ct); // the rows went with the review; so do the files
        return removed;
    }

    public async Task<IReadOnlyList<ReviewDto>> ListOwnAsync(Guid customerId, CancellationToken ct = default) =>
        await WithPhotosAsync(await Project(db.Set<ProductReview>().AsNoTracking().Where(r => r.CustomerId == customerId).OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id)).ToListAsync(ct), ct);

    public async Task<PagedResult<ReviewDto>> ListAsync(ReviewStatus? status, Guid? productId, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ProductReview>().AsNoTracking().Where(r => (status == null || r.Status == status) && (productId == null || r.ProductId == productId));
        var total = await query.CountAsync(ct);
        var items = await WithPhotosAsync(await Project(query.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id).Skip((p - 1) * size).Take(size)).ToListAsync(ct), ct);
        return new PagedResult<ReviewDto>(items, p, size, total);
    }

    public async Task<ReviewDto?> SetStatusAsync(Guid reviewId, ReviewStatus status, string? actor, CancellationToken ct = default)
    {
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null) return null;
        review.Status = status; review.ModeratedUtc = DateTimeOffset.UtcNow; review.ModeratedBy = actor; review.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(reviewId, ct);
    }

    public async Task<ReviewDto?> SetReplyAsync(Guid reviewId, string body, string? actor, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(new ReviewReplyInput { Body = body ?? "" });
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null) return null;
        var first = review.Reply is null;
        review.Reply = body!.Trim(); review.RepliedUtc = DateTimeOffset.UtcNow; review.RepliedBy = actor; review.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (first) await TellReviewerAsync(review, ct); // a changed reply is not sent again: fixing a typo should not email them twice
        return await ToDtoAsync(reviewId, ct);
    }

    /// <summary>Emails the reviewer that the shop has replied. Never fails the reply: the reply is already saved.</summary>
    private async Task TellReviewerAsync(ProductReview review, CancellationToken ct)
    {
        if (!options.Value.SendReviewReplies) return;
        try
        {
            var to = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == review.CustomerId).Select(c => c.Email).FirstOrDefaultAsync(ct);
            var product = await db.Set<Product>().AsNoTracking().Where(p => p.Id == review.ProductId).Select(p => new { p.Name, p.Slug }).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(to) || product is null) return;
            await email.SendAsync(new EmailMessage(to, EcommerceEmailTemplates.ReviewReplied, new Dictionary<string, object?>
            {
                ["email"] = to, ["displayName"] = review.DisplayName, ["productName"] = product.Name, ["productSlug"] = product.Slug ?? "", ["productId"] = review.ProductId.ToString(),
                ["rating"] = review.Rating, ["reviewTitle"] = review.Title ?? "", ["reply"] = review.Reply, ["published"] = review.Status == ReviewStatus.Approved,
            }), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "The review reply email for review {ReviewId} could not be sent.", review.Id); } // never the address
    }

    public async Task<ReviewDto?> DeleteReplyAsync(Guid reviewId, CancellationToken ct = default)
    {
        var review = await db.Set<ProductReview>().FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (review is null) return null;
        if (review.Reply is not null)
        {
            review.Reply = null; review.RepliedUtc = null; review.RepliedBy = null; review.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return await ToDtoAsync(reviewId, ct);
    }

    public async Task<bool> DeleteAsync(Guid reviewId, CancellationToken ct = default)
    {
        var hadPhotos = await db.Set<ReviewPhoto>().AnyAsync(p => p.ReviewId == reviewId, ct);
        var removed = await db.DeleteTrackedAsync<ProductReview>(r => r.Id == reviewId, ct) > 0;
        if (removed && hadPhotos) await store.DeleteFolderAsync($"reviews/{reviewId}", ct);
        return removed;
    }

    private IQueryable<ReviewDto> Project(IQueryable<ProductReview> reviews) =>
        from r in reviews
        select new ReviewDto(r.Id, r.ProductId, db.Set<Product>().Where(p => p.Id == r.ProductId).Select(p => p.Name).FirstOrDefault(), r.CustomerId, r.DisplayName,
            r.Rating, r.Title, r.Body, r.Status, r.VerifiedPurchase, r.CreatedUtc, r.ModeratedUtc, r.ModeratedBy, r.Reply, r.RepliedUtc, r.RepliedBy);

    private async Task<ReviewDto?> ToDtoAsync(Guid id, CancellationToken ct) => (await WithPhotosAsync(await Project(db.Set<ProductReview>().AsNoTracking().Where(r => r.Id == id)).ToListAsync(ct), ct)).FirstOrDefault();

    /// <summary>Each review's photos, oldest first. A review with none is left out of the result.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<ReviewPhotoDto>>> PhotosAsync(IReadOnlyCollection<Guid> reviewIds, CancellationToken ct)
    {
        if (reviewIds.Count == 0) return [];
        var rows = await db.Set<ReviewPhoto>().AsNoTracking().Where(p => reviewIds.Contains(p.ReviewId)).OrderBy(p => p.CreatedUtc).ThenBy(p => p.Id).ToListAsync(ct);
        return rows.GroupBy(p => p.ReviewId).ToDictionary(g => g.Key, g => (IReadOnlyList<ReviewPhotoDto>)g.Select(ReviewPhotoService.ToDto).ToList());
    }

    private async Task<List<ReviewDto>> WithPhotosAsync(List<ReviewDto> reviews, CancellationToken ct)
    {
        var photos = await PhotosAsync(reviews.Select(r => r.Id).ToList(), ct);
        return reviews.Select(r => r with { Photos = photos.GetValueOrDefault(r.Id, []) }).ToList();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task<string> NameAsync(Guid customerId, string? given, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(given)) return given.Trim();
        var c = await db.Set<Customer>().AsNoTracking().Where(x => x.Id == customerId).Select(x => new { x.FirstName, x.LastName }).FirstOrDefaultAsync(ct);
        var first = c?.FirstName?.Trim();
        if (string.IsNullOrEmpty(first)) return "A customer";
        var last = c?.LastName?.Trim();
        return string.IsNullOrEmpty(last) ? first : $"{first} {char.ToUpperInvariant(last[0])}.";
    }

    /// <summary>A bought-it check: an order on the customer's account that was not cancelled, with the product or any of its variants on a line.</summary>
    private async Task<bool> HasBoughtAsync(Guid customerId, Guid productId, CancellationToken ct)
    {
        var ids = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ParentProductId == productId).Select(v => v.ProductId).ToListAsync(ct);
        ids.Add(productId);
        return await db.Set<OrderLine>().AsNoTracking().AnyAsync(l => ids.Contains(l.ProductId)
            && db.Set<Order>().Any(o => o.Id == l.OrderId && o.CustomerId == customerId && o.Stage != OrderStage.Cancelled), ct);
    }
}

/// <summary>The reviews part of an export and an erasure: the person's reviews are listed, and deleted on erasure (a rating cannot stand without its author).</summary>
internal sealed class ReviewCustomerData(PiranhaCommerceDbContext db, IFileStore store) : ICustomerDataContributor
{
    public string Name => "reviews";

    public Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var reviews = await db.Set<ProductReview>().AsNoTracking().Where(r => r.CustomerId == subject.CustomerId).OrderBy(r => r.CreatedUtc)
            .Select(r => new { r.ProductId, r.DisplayName, r.Rating, r.Title, r.Body, Status = r.Status.ToString(), r.VerifiedPurchase, r.CreatedUtc, r.Reply, r.RepliedUtc }).ToListAsync(ct);
        var votes = await db.Set<ReviewHelpfulVote>().AsNoTracking().Where(v => v.CustomerId == subject.CustomerId).OrderBy(v => v.CreatedUtc)
            .Select(v => new { v.ReviewId, v.CreatedUtc }).ToListAsync(ct);
        var photos = await db.Set<ReviewPhoto>().AsNoTracking().Where(p => db.Set<ProductReview>().Any(r => r.Id == p.ReviewId && r.CustomerId == subject.CustomerId))
            .OrderBy(p => p.CreatedUtc).Select(p => new { p.ReviewId, p.FileName, p.ContentType, p.SizeBytes, p.CreatedUtc }).ToListAsync(ct);
        return reviews.Count == 0 && votes.Count == 0 ? null : new { Reviews = reviews, HelpfulVotes = votes, Photos = photos };
    }

    public async Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var withPhotos = await db.Set<ReviewPhoto>().Where(p => db.Set<ProductReview>().Any(r => r.Id == p.ReviewId && r.CustomerId == subject.CustomerId)).Select(p => p.ReviewId).Distinct().ToListAsync(ct);
        var n = await db.Set<ReviewHelpfulVote>().Where(v => v.CustomerId == subject.CustomerId).ExecuteDeleteAsync(ct)
            + await db.Set<ProductReview>().Where(r => r.CustomerId == subject.CustomerId).ExecuteDeleteAsync(ct);
        foreach (var id in withPhotos) await store.DeleteFolderAsync($"reviews/{id}", ct); // the photos are the person's too
        return n;
    }
}

internal sealed class ReviewEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/reviews").WithTags("Reviews").RequirePermission(Permissions.CustomersView);
        g.MapGet("/", async (ReviewStatus? status, Guid? productId, int? page, int? pageSize, IReviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(status, productId, page, pageSize, ct)));
        g.MapPut("/{id:guid}/status", async (Guid id, ReviewStatusInput input, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.SetStatusAsync(id, input.Status, user.Identity?.Name, ct) is { } r ? Results.Ok(r) : Results.NotFound()).RequirePermission(Permissions.ReviewsModerate);
        g.MapPut("/{id:guid}/reply", async (Guid id, ReviewReplyInput input, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.SetReplyAsync(id, input.Body, user.Identity?.Name, ct) is { } r ? Results.Ok(r) : Results.NotFound()).RequirePermission(Permissions.ReviewsModerate);
        g.MapDelete("/{id:guid}/reply", async (Guid id, IReviewService svc, CancellationToken ct) =>
            await svc.DeleteReplyAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound()).RequirePermission(Permissions.ReviewsModerate);
        g.MapDelete("/{id:guid}", async (Guid id, IReviewService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.ReviewsModerate);
    }
}

internal sealed class ReviewStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // Anyone may read approved reviews.
        storefront.MapGet("/products/{productId:guid}/reviews", async (Guid productId, int? page, int? pageSize, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.ListPublicAsync(productId, page, pageSize, user.CustomerId(), ct) is { } r ? Results.Ok(r) : Results.NotFound()).AllowAnonymous().WithTags("Storefront reviews");
        storefront.MapGet("/products/{productId:guid}/review-summary", async (Guid productId, IReviewService svc, CancellationToken ct) =>
            await svc.SummaryAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound()).AllowAnonymous().WithTags("Storefront reviews");

        // Writing needs a signed-in shopper, and only ever touches their own reviews.
        var mine = storefront.MapGroup("/reviews").WithTags("Storefront reviews").AddEndpointFilter<ActiveCustomerFilter>();
        mine.MapGet("/", async (ClaimsPrincipal user, IReviewService svc, CancellationToken ct) => Results.Ok(await svc.ListOwnAsync(user.CustomerId()!.Value, ct)));
        mine.MapPost("/", async (ReviewInput input, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
        {
            var r = await svc.CreateAsync(user.CustomerId()!.Value, input, ct);
            return Results.Created($"/api/v1/storefront/reviews/{r.Id}", r);
        });
        mine.MapPut("/{id:guid}", async (Guid id, ReviewUpdateInput input, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.UpdateOwnAsync(user.CustomerId()!.Value, id, input, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        mine.MapPut("/{id:guid}/helpful", async (Guid id, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.SetHelpfulAsync(user.CustomerId()!.Value, id, true, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        mine.MapDelete("/{id:guid}/helpful", async (Guid id, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.SetHelpfulAsync(user.CustomerId()!.Value, id, false, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        mine.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IReviewService svc, CancellationToken ct) =>
            await svc.DeleteOwnAsync(user.CustomerId()!.Value, id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
