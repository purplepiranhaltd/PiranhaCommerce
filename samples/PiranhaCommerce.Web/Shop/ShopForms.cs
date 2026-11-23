using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// What the shop's forms post to. Each one does its work, then sends the browser to a page (post, redirect, get), so a refresh never repeats it.
/// A problem goes back to the page with a message. Every route needs the antiforgery token the page's form carries (a post without it is refused),
/// and every one is rate limited per client, because creating a basket costs a row.
///
/// A basket is looked up through the browser's cookie and then checked: it must still exist, be open and have no owner. A cookie holding any other
/// basket's id is ignored, and a new basket is started.
/// </summary>
internal static class ShopForms
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var shop = app.MapGroup("");
        shop.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ShopRateLimiter>().Allow(context.HttpContext)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status429TooManyRequests));

        shop.MapPost("/basket/add", async (IFormCollection form, ShopSession session, IBasketService baskets, IGuestBasketService guests, IShopCatalogueService catalogue, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            try
            {
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaValidationException("productId", "Choose what to add.");
                var quantity = Count(form["quantity"], 1);
                if (quantity < 1) throw new PiranhaValidationException("quantity", "Choose how many you want.");

                // What is on the shelf: a sold-out shade or a quantity beyond what is left is refused here with a reason (checkout checks again).
                var product = await catalogue.GetAsync(productId, null, null, ct) ?? throw new PiranhaValidationException("productId", "That product is not on sale.");
                var variant = product.Variants.FirstOrDefault(v => v.ProductId == productId);
                if (variant is null && product.HasVariants) throw new PiranhaValidationException("productId", "Choose an option first.");
                if (!(variant?.Available ?? product.Available)) throw new PiranhaValidationException("productId", "Sorry, that is sold out.");
                var left = variant is not null ? variant.Left : product.Left;

                var basket = session.BasketId is { } id && await guests.IsGuestBasketAsync(id, ct) && await baskets.GetAsync(id, ct) is { Status: BasketStatus.Open } b ? b : null;
                if (basket is null)
                {
                    basket = await baskets.CreateAsync(new BasketCreateInput(), ct);
                    session.SetBasketId(basket.Id);
                }

                var have = basket.Lines.FirstOrDefault(l => l.ProductId == productId)?.Quantity ?? 0;
                if (left is { } few && have + quantity > few)
                    throw new PiranhaValidationException("quantity", few == 0 ? "Sorry, that is sold out." : $"Only {few} left" + (have > 0 ? $", and you already have {have} in your basket." : "."));
                await baskets.SetLineAsync(basket.Id, productId, new BasketLineInput { Quantity = Math.Min(have + quantity, BasketLineInput.Max) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        shop.MapPost("/basket/set", async (IFormCollection form, ShopSession session, IBasketService baskets, IGuestBasketService guests, CancellationToken ct) =>
        {
            try
            {
                if (await Own(session, baskets, guests, ct) is { } id && Guid.TryParse(form["productId"], out var productId))
                    await baskets.SetLineAsync(id, productId, new BasketLineInput { Quantity = Math.Clamp(Count(form["quantity"], 0), 0, 99) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });

        shop.MapPost("/basket/promo", async (IFormCollection form, ShopSession session, IBasketService baskets, IGuestBasketService guests, CancellationToken ct) =>
        {
            try
            {
                if (await Own(session, baskets, guests, ct) is { } id)
                    await baskets.SetPromotionAsync(id, form["remove"].Count > 0 ? null : form["code"].ToString().Trim(), ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });

        // Checkout, step 1: where the order goes.
        shop.MapPost("/checkout/details", async (IFormCollection form, ShopSession session, IBasketService baskets, IGuestBasketService guests,
            Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.AllowGuestCheckout) return Results.Redirect("/checkout");
            if (await Own(session, baskets, guests, ct) is null) return Results.Redirect("/basket");

            string Field(string name) => form[name].ToString().Trim();
            var values = new[] { "email", "recipient", "line1", "line2", "city", "region", "postalCode", "countryCode" }.ToDictionary(n => n, Field);
            try
            {
                var country = values["countryCode"].ToUpperInvariant();
                if (!ShopCountries.All.Any(c => c.Code == country)) throw new PiranhaValidationException("countryCode", "Choose a country we deliver to.");
                var address = new AddressInput
                {
                    Recipient = Blank(values["recipient"]), Line1 = values["line1"], Line2 = Blank(values["line2"]), City = values["city"],
                    Region = Blank(values["region"]), PostalCode = Blank(values["postalCode"]), CountryCode = country,
                };
                ModelValidator.ValidateOrThrow(address);
                ModelValidator.ValidateOrThrow(new CheckoutInput { Email = values["email"], ShippingAddress = address });

                session.SetDetails(new CheckoutDetails(values["email"], address.Recipient, address.Line1, address.Line2, address.City, address.Region, address.PostalCode, country));
                return Results.Redirect("/checkout/delivery");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/checkout", new Flash(Message(e), null, values))); }
        });

        // Checkout, step 2: the delivery method, and place the order.
        shop.MapPost("/checkout/place", async (IFormCollection form, ShopSession session, IBasketService baskets, IGuestBasketService guests, IOrderService orders,
            Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
        {
            if (!options.Value.AllowGuestCheckout) return Results.Redirect("/checkout");
            if (await Own(session, baskets, guests, ct) is not { } basketId) return Results.Redirect("/basket");
            if (session.Details is not { } d) return Results.Redirect("/checkout");

            try
            {
                if (!Guid.TryParse(form["shippingMethodId"], out var method)) throw new PiranhaValidationException("shippingMethodId", "Choose a delivery method.");
                var order = await orders.CheckoutAsync(basketId, new CheckoutInput
                {
                    Email = d.Email, CustomerId = null, ShippingMethodId = method, Notes = Blank(form["notes"].ToString().Trim()),
                    ShippingAddress = new AddressInput { Recipient = d.Recipient, Line1 = d.Line1, Line2 = d.Line2, City = d.City, Region = d.Region, PostalCode = d.PostalCode, CountryCode = d.CountryCode },
                }, null, ct);
                if (order is null) return Results.Redirect("/basket");

                session.SetLastOrder(order.Id);
                session.ForgetBasket();
                session.ForgetDetails();
                return Results.Redirect("/order/confirmed");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/checkout/delivery", new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });
    }

    private static async Task<Guid?> Own(ShopSession session, IBasketService baskets, IGuestBasketService guests, CancellationToken ct) =>
        session.BasketId is { } id && await guests.IsGuestBasketAsync(id, ct) && await baskets.GetAsync(id, ct) is { Status: BasketStatus.Open } ? id : null;

    private static int Count(string? text, int fallback) => int.TryParse(text, out var n) ? n : fallback;

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>The service's own words, one sentence per problem.</summary>
    private static string Message(PiranhaValidationException e) => string.Join(" ", e.Errors.Values.SelectMany(v => v));
}
