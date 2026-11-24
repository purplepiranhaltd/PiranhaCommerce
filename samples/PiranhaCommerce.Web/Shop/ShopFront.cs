using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// What the shop's pages ask for, one call at a time. A server-rendered page and its layout load their data at the same moment, and both
/// would use the request's single database connection; this runs the calls in turn, and remembers the category tree for the request.
/// </summary>
public sealed record BasketLineView(BasketLineDto Line, QuoteLineDto? Priced, string? Slug, ShopImageDto? Image, string? Variant);

/// <summary>The basket as the shopper sees it: the lines and what they will cost (tax and any code included), before delivery is chosen.</summary>
public sealed record BasketView(BasketDto Basket, QuoteDto? Quote, IReadOnlyList<BasketLineView> Lines);

/// <summary>A product suggested for the basket, with its price for this shopper (tax included when a country is known).</summary>
public sealed record CrossSellItem(SuggestedProductDto Product, ShopperPriceDto? Price);

/// <summary>A way of delivering this basket to the address given, with what the whole order would then cost.</summary>
public sealed record DeliveryChoice(ShippingOptionDto Option, QuoteDto Quote);

public sealed class ShopFront(
    IShopCatalogueService catalogue, IProductRelationService relations, IGroupPricingService pricing, IBasketService baskets, ShopBaskets shopBaskets,
    IOrderService orders, ICustomerAccountService accounts, IWishlistService wishlists, IReviewService reviews, ShopSession session, IOptions<ShopOptions> shop)
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private IReadOnlyList<ShopCategoryDto>? _categories;

    private async Task<T> InTurn<T>(Func<Task<T>> work)
    {
        await _turn.WaitAsync();
        try { return await work(); }
        finally { _turn.Release(); }
    }

    public Task<IReadOnlyList<ShopCategoryDto>> CategoriesAsync() => InTurn(async () => _categories ??= await catalogue.CategoriesAsync());

    public Task<PagedResult<ShopProductSummaryDto>> ListAsync(ShopProductQuery query) => InTurn(async () => await catalogue.ListAsync(query, await CustomerIdAsync()));

    /// <summary>The filters still available for this listing, with counts.</summary>
    public Task<IReadOnlyList<ShopFacetDto>> FacetsAsync(ShopProductQuery query) => InTurn(async () => await catalogue.FacetsAsync(query));

    /// <summary>The tags that have products on sale, for the "Browse by tag" row.</summary>
    public Task<IReadOnlyList<ShopTagDto>> TagsAsync() => InTurn(async () => await catalogue.TagsAsync());

    /// <summary>The brands on sale (for a brand's own heading and description).</summary>
    public Task<IReadOnlyList<ShopBrandDto>> BrandsAsync() => InTurn(async () => await catalogue.BrandsAsync());

    public Task<ShopProductDto?> ProductAsync(string slug, string? country) => InTurn(async () => await catalogue.GetBySlugAsync(slug, await CustomerIdAsync(), country));

    public Task<IReadOnlyList<SuggestedProductDto>> RelatedAsync(Guid productId) =>
        InTurn<IReadOnlyList<SuggestedProductDto>>(async () => (await relations.ForProductAsync(productId, await CustomerIdAsync()))?.Related ?? []);

    public Task<Dictionary<Guid, ShopperPriceDto>> PricesAsync(IReadOnlyCollection<Guid> ids, string? country) =>
        InTurn(async () => (await pricing.PricesAsync(await CustomerIdAsync(), ids, country)).ToDictionary(p => p.ProductId));

    private async Task<Guid?> CustomerIdAsync() => (await shopBaskets.ShopperAsync())?.Id;

    /// <summary>Who is signed in, or null.</summary>
    public Task<Shopper?> ShopperAsync() => InTurn(() => shopBaskets.ShopperAsync());

    public Task<int> BasketCountAsync() => InTurn(async () => await shopBaskets.CurrentAsync() is { } b ? b.Lines.Sum(l => l.Quantity) : 0);

    public Task<BasketView?> BasketAsync() => InTurn(async () =>
    {
        if (await shopBaskets.CurrentAsync() is not { } basket) return null;
        var id = basket.Id;
        var customerId = await CustomerIdAsync();
        // Line prices, tax and any code do not depend on the delivery chosen, but a quote needs a method (when the shop has any), so use the first that serves the home country.
        var country = shop.Value.CountryCode;
        var firstMethod = (await baskets.ShippingOptionsAsync(id, country) ?? []).FirstOrDefault()?.Id;
        QuoteDto? quote = null;
        try { quote = await baskets.QuoteAsync(id, new QuoteInput { ShippingMethodId = firstMethod, CountryCode = country }); }
        catch (PiranhaCommerceValidationException) { } // no method serves the home country: lines are shown without tax detail
        var lines = new List<BasketLineView>();
        foreach (var line in basket.Lines)
        {
            var product = await catalogue.GetAsync(line.ProductId, customerId, shop.Value.CountryCode);
            var variant = product?.Variants.FirstOrDefault(v => v.ProductId == line.ProductId);
            var image = variant?.Images.FirstOrDefault() ?? product?.Images.FirstOrDefault();
            lines.Add(new BasketLineView(line, quote?.Lines?.FirstOrDefault(q => q.ProductId == line.ProductId), product?.Slug, image,
                variant is null ? null : string.Join(", ", variant.Values.Select(v => v.Value))));
        }
        return new BasketView(basket, quote, lines);
    });

    /// <summary>The ways to deliver to this address, each with the order total it gives. Null when the shopper has no basket.</summary>
    public Task<IReadOnlyList<DeliveryChoice>?> DeliveryAsync(string countryCode, string? postalCode) => InTurn<IReadOnlyList<DeliveryChoice>?>(async () =>
    {
        if (await shopBaskets.CurrentAsync() is not { } current) return null;
        var id = current.Id;
        var options = await baskets.ShippingOptionsAsync(id, countryCode, postalCode) ?? [];
        var choices = new List<DeliveryChoice>();
        foreach (var o in options)
            if (await baskets.QuoteAsync(id, new QuoteInput { ShippingMethodId = o.Id, CountryCode = countryCode, PostalCode = postalCode }) is { } q)
                choices.Add(new DeliveryChoice(o, q));
        return choices;
    });

    /// <summary>The order this browser has just placed: a guest's (which belongs to no customer) or the signed-in shopper's own.</summary>
    public Task<OrderDto?> LastOrderAsync() => InTurn(async () =>
    {
        var me = await CustomerIdAsync();
        return session.LastOrderId is { } id && await orders.GetAsync(id) is { } order && (order.CustomerId is null || order.CustomerId == me) ? order : null;
    });

    public Task<ProfileDto?> ProfileAsync() => InTurn(async () => await CustomerIdAsync() is { } id ? await accounts.GetProfileAsync(id) : null);

    /// <summary>The signed-in shopper's own orders, newest first.</summary>
    public Task<PagedResult<OrderSummaryDto>?> OrdersAsync(int page) => InTurn(async () =>
        await CustomerIdAsync() is { } id ? await orders.ListForCustomerAsync(id, page, 10) : null);

    /// <summary>One of the signed-in shopper's own orders. Anyone else's, or one that does not exist, is null.</summary>
    public Task<OrderDto?> OrderAsync(Guid orderId) => InTurn(async () =>
        await CustomerIdAsync() is { } id ? await orders.GetForCustomerAsync(id, orderId) : null);

    /// <summary>The approved reviews of a product, newest first, with the star totals.</summary>
    public Task<PublicReviewsDto?> ReviewsAsync(Guid productId) => InTurn(async () => await reviews.ListPublicAsync(productId, 1, 10, await CustomerIdAsync()));

    /// <summary>The signed-in shopper's own review of this product (whatever state it is in), or null.</summary>
    public Task<ReviewDto?> OwnReviewAsync(Guid productId) => InTurn(async () =>
        await CustomerIdAsync() is { } id ? (await reviews.ListOwnAsync(id)).FirstOrDefault(r => r.ProductId == productId) : null);

    /// <summary>The signed-in shopper's wishlist, newest first (empty when not signed in).</summary>
    public Task<IReadOnlyList<WishlistItemDto>> WishlistAsync() => InTurn<IReadOnlyList<WishlistItemDto>>(async () =>
        await CustomerIdAsync() is { } id ? await wishlists.ListAsync(id) : []);

    /// <summary>What goes well with what is in the basket, priced for this shopper with tax.</summary>
    public Task<IReadOnlyList<CrossSellItem>> CrossSellAsync(IReadOnlyCollection<Guid> productIds) => InTurn<IReadOnlyList<CrossSellItem>>(async () =>
    {
        var customerId = await CustomerIdAsync();
        var found = await relations.CrossSellForBasketAsync(productIds, customerId);
        if (found.Count == 0) return [];
        var prices = (await pricing.PricesAsync(customerId, found.Select(f => f.ProductId).ToList(), shop.Value.CountryCode)).ToDictionary(p => p.ProductId);
        return found.Select(f => new CrossSellItem(f, prices.GetValueOrDefault(f.ProductId))).ToList();
    });
}
