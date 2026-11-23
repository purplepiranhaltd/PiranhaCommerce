using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// What the shop's pages ask for, one call at a time. A server-rendered page and its layout load their data at the same moment, and both
/// would use the request's single database connection; this runs the calls in turn, and remembers the category tree for the request.
/// </summary>
public sealed record BasketLineView(BasketLineDto Line, QuoteLineDto? Priced, string? Slug, ShopImageDto? Image, string? Variant);

/// <summary>The basket as the shopper sees it: the lines and what they will cost (tax and any code included), before delivery is chosen.</summary>
public sealed record BasketView(BasketDto Basket, QuoteDto? Quote, IReadOnlyList<BasketLineView> Lines);

/// <summary>A way of delivering this basket to the address given, with what the whole order would then cost.</summary>
public sealed record DeliveryChoice(ShippingOptionDto Option, QuoteDto Quote);

public sealed class ShopFront(
    IShopCatalogueService catalogue, IProductRelationService relations, IGroupPricingService pricing, IBasketService baskets, IGuestBasketService guestBaskets,
    IOrderService orders, ShopSession session, IOptions<ShopOptions> shop)
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

    public Task<PagedResult<ShopProductSummaryDto>> ListAsync(ShopProductQuery query) => InTurn(() => catalogue.ListAsync(query, null));

    public Task<ShopProductDto?> ProductAsync(string slug, string? country) => InTurn(() => catalogue.GetBySlugAsync(slug, null, country));

    public Task<IReadOnlyList<SuggestedProductDto>> RelatedAsync(Guid productId) =>
        InTurn<IReadOnlyList<SuggestedProductDto>>(async () => (await relations.ForProductAsync(productId))?.Related ?? []);

    public Task<Dictionary<Guid, ShopperPriceDto>> PricesAsync(IReadOnlyCollection<Guid> ids, string? country) =>
        InTurn(async () => (await pricing.PricesAsync(null, ids, country)).ToDictionary(p => p.ProductId));

    /// <summary>The basket this browser may use: the one in its cookie, if it still exists, is open and belongs to no one. Anything else is "no basket".</summary>
    private async Task<Guid?> OwnBasketIdAsync()
    {
        if (session.BasketId is not { } id) return null;
        if (!await guestBaskets.IsGuestBasketAsync(id)) return null;
        return await baskets.GetAsync(id) is { Status: BasketStatus.Open } ? id : null;
    }

    public Task<int> BasketCountAsync() => InTurn(async () =>
        await OwnBasketIdAsync() is { } id && await baskets.GetAsync(id) is { } b ? b.Lines.Sum(l => l.Quantity) : 0);

    public Task<BasketView?> BasketAsync() => InTurn(async () =>
    {
        if (await OwnBasketIdAsync() is not { } id || await baskets.GetAsync(id) is not { } basket) return null;
        // Line prices, tax and any code do not depend on the delivery chosen, but a quote needs a method (when the shop has any), so use the first that serves the home country.
        var country = shop.Value.CountryCode;
        var firstMethod = (await baskets.ShippingOptionsAsync(id, country) ?? []).FirstOrDefault()?.Id;
        QuoteDto? quote = null;
        try { quote = await baskets.QuoteAsync(id, new QuoteInput { ShippingMethodId = firstMethod, CountryCode = country }); }
        catch (PiranhaValidationException) { } // no method serves the home country: lines are shown without tax detail
        var lines = new List<BasketLineView>();
        foreach (var line in basket.Lines)
        {
            var product = await catalogue.GetAsync(line.ProductId, null, shop.Value.CountryCode);
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
        if (await OwnBasketIdAsync() is not { } id) return null;
        var options = await baskets.ShippingOptionsAsync(id, countryCode, postalCode) ?? [];
        var choices = new List<DeliveryChoice>();
        foreach (var o in options)
            if (await baskets.QuoteAsync(id, new QuoteInput { ShippingMethodId = o.Id, CountryCode = countryCode, PostalCode = postalCode }) is { } q)
                choices.Add(new DeliveryChoice(o, q));
        return choices;
    });

    /// <summary>The order this browser has just placed (a guest's order belongs to no customer, so only those are shown here).</summary>
    public Task<OrderDto?> LastOrderAsync() => InTurn(async () =>
        session.LastOrderId is { } id && await orders.GetAsync(id) is { CustomerId: null } order ? order : null);
}
