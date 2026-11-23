using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// What the shop's forms post to. Each one does its work, then sends the browser to a page (post, redirect, get), so a refresh never repeats it.
/// A problem goes back to the page with a message. Every route needs the antiforgery token the page's form carries (a post without it is refused),
/// and every one is rate limited per client: starting a basket costs a row, and sign-in is what a password-guessing script would hit.
///
/// Which basket is used, and who the shopper is, is decided by <see cref="ShopBaskets"/>.
/// </summary>
internal static class ShopForms
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapBasket(app.MapGroup("/basket").AddEndpointFilter(Limit(account: false)));
        MapCheckout(app.MapGroup("/checkout").AddEndpointFilter(Limit(account: false)));
        MapAccount(app.MapGroup("/account").AddEndpointFilter(Limit(account: true)));
    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Limit(bool account) => async (context, next) =>
        context.HttpContext.RequestServices.GetRequiredService<ShopRateLimiter>().Allow(context.HttpContext, account)
            ? await next(context)
            : Results.StatusCode(StatusCodes.Status429TooManyRequests);

    private static void MapBasket(RouteGroupBuilder shop)
    {
        shop.MapPost("/add", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IBasketService baskets, IShopCatalogueService catalogue, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            try
            {
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaValidationException("productId", "Choose what to add.");
                var quantity = Count(form["quantity"], 1);
                if (quantity < 1) throw new PiranhaValidationException("quantity", "Choose how many you want.");

                // What is on the shelf: a sold-out shade or a quantity beyond what is left is refused here with a reason (checkout checks again).
                var customerId = (await shopBaskets.ShopperAsync(ct))?.Id;
                var product = await catalogue.GetAsync(productId, customerId, null, ct) ?? throw new PiranhaValidationException("productId", "That product is not on sale.");
                var variant = product.Variants.FirstOrDefault(v => v.ProductId == productId);
                if (variant is null && product.HasVariants) throw new PiranhaValidationException("productId", "Choose an option first.");
                if (!(variant?.Available ?? product.Available)) throw new PiranhaValidationException("productId", "Sorry, that is sold out.");
                var left = variant is not null ? variant.Left : product.Left;

                var basket = await shopBaskets.CurrentOrNewAsync(ct);
                var have = basket.Lines.FirstOrDefault(l => l.ProductId == productId)?.Quantity ?? 0;
                if (left is { } few && have + quantity > few)
                    throw new PiranhaValidationException("quantity", few == 0 ? "Sorry, that is sold out." : $"Only {few} left" + (have > 0 ? $", and you already have {have} in your basket." : "."));

                await baskets.SetLineAsync(basket.Id, productId, new BasketLineInput { Quantity = Math.Min(have + quantity, BasketLineInput.Max) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        shop.MapPost("/set", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IBasketService baskets, CancellationToken ct) =>
        {
            try
            {
                if (await shopBaskets.CurrentAsync(ct) is { } basket && Guid.TryParse(form["productId"], out var productId))
                    await baskets.SetLineAsync(basket.Id, productId, new BasketLineInput { Quantity = Math.Clamp(Count(form["quantity"], 0), 0, 99) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });

        shop.MapPost("/promo", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IBasketService baskets, CancellationToken ct) =>
        {
            try
            {
                if (await shopBaskets.CurrentAsync(ct) is { } basket)
                    await baskets.SetPromotionAsync(basket.Id, form["remove"].Count > 0 ? null : form["code"].ToString().Trim(), ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });
    }

    private static void MapCheckout(RouteGroupBuilder checkout)
    {
        // Step 1: where the order goes. A signed-in shopper's email is their account's, whatever the form says.
        checkout.MapPost("/details", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets,
            Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
        {
            var shopper = await shopBaskets.ShopperAsync(ct);
            if (shopper is null && !options.Value.AllowGuestCheckout) return Results.Redirect("/account/sign-in?returnTo=/checkout");
            if (await shopBaskets.CurrentAsync(ct) is null) return Results.Redirect("/basket");

            string Field(string name) => form[name].ToString().Trim();
            var values = new[] { "email", "recipient", "line1", "line2", "city", "region", "postalCode", "countryCode" }.ToDictionary(n => n, Field);
            if (shopper is not null) values["email"] = shopper.Email;
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

        // Step 2: the delivery method, and place the order. A signed-in shopper's order is theirs, with their account's email.
        checkout.MapPost("/place", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IOrderService orders,
            Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
        {
            var shopper = await shopBaskets.ShopperAsync(ct);
            if (shopper is null && !options.Value.AllowGuestCheckout) return Results.Redirect("/account/sign-in?returnTo=/checkout");
            if (await shopBaskets.CurrentAsync(ct) is not { } basket) return Results.Redirect("/basket");
            if (session.Details is not { } d) return Results.Redirect("/checkout");

            try
            {
                if (!Guid.TryParse(form["shippingMethodId"], out var method)) throw new PiranhaValidationException("shippingMethodId", "Choose a delivery method.");
                var order = await orders.CheckoutAsync(basket.Id, new CheckoutInput
                {
                    Email = shopper?.Email ?? d.Email, CustomerId = shopper?.Id, ShippingMethodId = method, Notes = Blank(form["notes"].ToString().Trim()),
                    ShippingAddress = new AddressInput { Recipient = d.Recipient, Line1 = d.Line1, Line2 = d.Line2, City = d.City, Region = d.Region, PostalCode = d.PostalCode, CountryCode = d.CountryCode },
                }, shopper?.Email, ct);
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

    private static void MapAccount(RouteGroupBuilder account)
    {
        account.MapPost("/sign-in", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerAccountService accounts, CancellationToken ct) =>
        {
            var returnTo = form["returnTo"].ToString();
            var email = form["email"].ToString().Trim();
            var result = await accounts.CheckAsync(email, form["password"].ToString(), ct);
            var typed = new Dictionary<string, string> { ["signInEmail"] = email };
            switch (result.Status)
            {
                case SignInStatus.Valid:
                    return await StartSessionAsync(session, shopBaskets, accounts, result, ShopSession.SafePath(returnTo, "/account"), ct);
                case SignInStatus.LockedOut:
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("Too many wrong attempts. Please wait a while, then try again.", null, typed)));
                case SignInStatus.Unverified:
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("Please follow the link we emailed you to confirm your address first.", null, typed)));
                default: // wrong password, no such account, no sign-in or inactive: one answer, so it cannot be used to find out who has an account
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("That email and password do not match an account.", null, typed)));
            }
        });

        account.MapPost("/register", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerAccountService accounts, CancellationToken ct) =>
        {
            var values = new Dictionary<string, string>
            {
                ["registerEmail"] = form["email"].ToString().Trim(), ["firstName"] = form["firstName"].ToString().Trim(), ["lastName"] = form["lastName"].ToString().Trim(),
            };
            var password = form["password"].ToString();
            try
            {
                await accounts.RegisterAsync(new RegisterInput { Email = values["registerEmail"], Password = password, FirstName = Blank(values["firstName"]), LastName = Blank(values["lastName"]) }, ct);
            }
            catch (PiranhaValidationException e) { return Results.Redirect(session.Back("/account/sign-in", new Flash(Message(e), null, values))); }
            catch (PiranhaConflictException)
            {
                // The email already belongs to a customer (perhaps from an earlier guest order). Say what to do without saying whether they have a password.
                return Results.Redirect(session.Back("/account/sign-in", new Flash("We could not create an account with that email. If you have shopped here before, use \"Forgot your password?\" to set one.", null, values)));
            }

            var result = await accounts.CheckAsync(values["registerEmail"], password, ct);
            return result.Status == SignInStatus.Valid
                ? await StartSessionAsync(session, shopBaskets, accounts, result, "/account", ct)
                : Results.Redirect(session.Back("/account/sign-in", new Flash(null, "Your account is ready. Follow the link we emailed you to confirm your address, then sign in.")));
        });

        // Signing out is a form like any other, so it carries the antiforgery token too.
        account.MapPost("/sign-out", async (IFormCollection form, ShopSession session) =>
        {
            await session.SignOutAsync();
            session.ForgetBasket();
            session.ForgetDetails();
            return Results.Redirect("/");
        });

        // Always the same answer, whether or not the address has an account.
        account.MapPost("/forgot", async (IFormCollection form, ShopSession session, ICustomerAccountService accounts, CancellationToken ct) =>
        {
            try { await accounts.RequestPasswordResetAsync(new ForgotPasswordInput { Email = form["email"].ToString().Trim() }, ct); }
            catch (PiranhaValidationException) { }
            return Results.Redirect(session.Back("/account/forgot", new Flash(null, "If that address has an account, we have emailed a link to choose a new password.")));
        });

        account.MapPost("/reset", async (IFormCollection form, ShopSession session, ICustomerAccountService accounts, CancellationToken ct) =>
        {
            var input = new ResetPasswordInput { Email = form["email"].ToString().Trim(), Token = form["token"].ToString().Trim(), NewPassword = form["password"].ToString() };
            try
            {
                return await accounts.ResetPasswordAsync(input, ct)
                    ? Results.Redirect(session.Back("/account/sign-in", new Flash(null, "Your password has been changed. You can sign in now.")))
                    : Results.Redirect(session.Back("/account/forgot", new Flash("That link is not valid or has expired. Ask for a new one.")));
            }
            catch (PiranhaValidationException e)
            {
                var back = "/account/reset?email=" + Uri.EscapeDataString(input.Email) + "&token=" + Uri.EscapeDataString(input.Token);
                return Results.Redirect(back + "&f=" + Uri.EscapeDataString(session.Protect(new Flash(Message(e)))));
            }
        });
    }

    /// <summary>The shopper is who they said: start their session, and bring their basket with them.</summary>
    private static async Task<IResult> StartSessionAsync(ShopSession session, ShopBaskets shopBaskets, ICustomerAccountService accounts, SignInResult result, string to, CancellationToken ct)
    {
        var profile = await accounts.GetProfileAsync(result.CustomerId!.Value, ct);
        await session.SignInAsync(ShopAuth.Principal(result, profile));
        await shopBaskets.AdoptAsync(result.CustomerId.Value, ct);
        return Results.Redirect(to);
    }

    private static int Count(string? text, int fallback) => int.TryParse(text, out var n) ? n : fallback;

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>The service's own words, one sentence per problem.</summary>
    private static string Message(PiranhaValidationException e) => string.Join(" ", e.Errors.Values.SelectMany(v => v));
}
