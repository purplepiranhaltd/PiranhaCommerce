using Microsoft.AspNetCore.Authentication;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// Who the shopper is and which basket is theirs, decided in one place so the pages and the forms can never disagree.
///
/// A basket is looked up from the browser's cookie and then proved against the database: for a guest it must have no owner, for a signed-in shopper it
/// must be theirs. A cookie holding any other basket's id is ignored. A signed-in shopper with no usable cookie gets the open basket they used last,
/// which is how it follows them to a new browser. Not safe to use from two components at once (they share one database connection): the pages
/// go through <see cref="ShopFront"/>, which takes turns.
/// </summary>
public sealed class ShopBaskets(ShopSession session, IBasketService baskets, IGuestBasketService guests)
{
    private Shopper? _shopper;
    private bool _read;

    public async Task<Shopper?> ShopperAsync(CancellationToken ct = default)
    {
        if (_read) return _shopper;
        var result = await session.AuthenticateAsync();
        _shopper = result.Succeeded ? ShopAuth.From(result.Principal) : null;
        _read = true;
        return _shopper;
    }

    /// <summary>The open basket this browser may use, or null.</summary>
    public async Task<BasketDto?> CurrentAsync(CancellationToken ct = default)
    {
        var shopper = await ShopperAsync(ct);
        if (session.BasketId is { } id)
        {
            var mine = shopper is null ? await guests.IsGuestBasketAsync(id, ct) : await baskets.IsOwnedByAsync(id, shopper.Id, ct);
            if (mine && await baskets.GetAsync(id, ct) is { Status: BasketStatus.Open } basket) return basket;
        }
        return shopper is null ? null : await baskets.OpenForCustomerAsync(shopper.Id, ct);
    }

    /// <summary>The basket to put something in: the current one, or a new one for this shopper (a customer's own when signed in).</summary>
    public async Task<BasketDto> CurrentOrNewAsync(CancellationToken ct = default)
    {
        var basket = await CurrentAsync(ct) ?? await baskets.CreateAsync(new BasketCreateInput { CustomerId = (await ShopperAsync(ct))?.Id }, ct);
        session.SetBasketId(basket.Id);
        return basket;
    }

    /// <summary>
    /// Right after sign-in: the basket they were filling as a guest becomes theirs, or is added to the one they already had, and the browser follows it.
    /// </summary>
    public async Task<BasketDto?> AdoptAsync(Guid customerId, CancellationToken ct = default)
    {
        var guest = session.BasketId is { } id && await guests.IsGuestBasketAsync(id, ct) && await baskets.GetAsync(id, ct) is { Status: BasketStatus.Open } g ? g : null;
        var existing = await baskets.OpenForCustomerAsync(customerId, ct);

        BasketDto? result = existing;
        if (guest is not null && existing is not null) result = await guests.MergeAsync(existing.Id, guest.Id, customerId, ct) ?? existing;
        else if (guest is not null) result = await baskets.ClaimAsync(guest.Id, customerId, ct) ? await baskets.GetAsync(guest.Id, ct) : null;

        if (result is not null) session.SetBasketId(result.Id); else session.ForgetBasket();
        return result;
    }
}
