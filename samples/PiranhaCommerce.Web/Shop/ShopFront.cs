using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// What the shop's pages ask for, one call at a time. A server-rendered page and its layout load their data at the same moment, and both
/// would use the request's single database connection; this runs the calls in turn, and remembers the category tree for the request.
/// </summary>
public sealed class ShopFront(IShopCatalogueService catalogue, IProductRelationService relations, IGroupPricingService pricing)
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
}
