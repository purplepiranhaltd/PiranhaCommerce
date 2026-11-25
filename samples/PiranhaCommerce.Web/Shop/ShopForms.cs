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
        MapEngagement(app.MapGroup("").AddEndpointFilter(Limit(account: false)));
        MapStockAlerts(app.MapGroup("/stock-alert").AddEndpointFilter(Limit(account: true))); // an open form that sends email: kept to the strict limit
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
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaCommerceValidationException("productId", "Choose what to add.");
                var quantity = Count(form["quantity"], 1);
                if (quantity < 1) throw new PiranhaCommerceValidationException("quantity", "Choose how many you want.");

                // What is on the shelf: a sold-out shade or a quantity beyond what is left is refused here with a reason (checkout checks again).
                var customerId = (await shopBaskets.ShopperAsync(ct))?.Id;
                var product = await catalogue.GetAsync(productId, customerId, null, ct) ?? throw new PiranhaCommerceValidationException("productId", "That product is not on sale.");
                var variant = product.Variants.FirstOrDefault(v => v.ProductId == productId);
                if (variant is null && product.HasVariants) throw new PiranhaCommerceValidationException("productId", "Choose an option first.");
                if (!(variant?.Available ?? product.Available)) throw new PiranhaCommerceValidationException("productId", "Sorry, that is sold out.");
                var left = variant is not null ? variant.Left : product.Left;

                var basket = await shopBaskets.CurrentOrNewAsync(ct);
                var have = basket.Lines.FirstOrDefault(l => l.ProductId == productId)?.Quantity ?? 0;
                if (left is { } few && have + quantity > few)
                    throw new PiranhaCommerceValidationException("quantity", few == 0 ? "Sorry, that is sold out." : $"Only {few} left" + (have > 0 ? $", and you already have {have} in your basket." : "."));

                await baskets.SetLineAsync(basket.Id, productId, new BasketLineInput { Quantity = Math.Min(have + quantity, BasketLineInput.Max) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e)))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        shop.MapPost("/set", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IBasketService baskets, CancellationToken ct) =>
        {
            try
            {
                if (await shopBaskets.CurrentAsync(ct) is { } basket && Guid.TryParse(form["productId"], out var productId))
                    await baskets.SetLineAsync(basket.Id, productId, new BasketLineInput { Quantity = Math.Clamp(Count(form["quantity"], 0), 0, 99) }, ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });

        shop.MapPost("/promo", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IBasketService baskets, CancellationToken ct) =>
        {
            try
            {
                if (await shopBaskets.CurrentAsync(ct) is { } basket)
                    await baskets.SetPromotionAsync(basket.Id, form["remove"].Count > 0 ? null : form["code"].ToString().Trim(), ct);
                return Results.Redirect("/basket");
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back("/basket", new Flash(Message(e)))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
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
                if (!ShopCountries.All.Any(c => c.Code == country)) throw new PiranhaCommerceValidationException("countryCode", "Choose a country we deliver to.");
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
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back("/checkout", new Flash(Message(e), null, values))); }
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
                if (!Guid.TryParse(form["shippingMethodId"], out var method)) throw new PiranhaCommerceValidationException("shippingMethodId", "Choose a delivery method.");
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
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back("/checkout/delivery", new Flash(Message(e)))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back("/basket", new Flash(e.Message))); }
        });
    }

    private static void MapAccount(RouteGroupBuilder account)
    {
        account.MapPost("/sign-in", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerAccountService accounts, CancellationToken ct) =>
        {
            var returnTo = form["returnTo"].ToString();
            var email = form["email"].ToString().Trim();
            var result = await accounts.SignInAsync(email, form["password"].ToString(), form["code"].ToString(), ct);
            var typed = new Dictionary<string, string> { ["signInEmail"] = email };
            switch (result.Status)
            {
                case SignInStatus.Valid:
                    return await StartSessionAsync(session, shopBaskets, accounts, result, ShopSession.SafePath(returnTo, "/account"), ct);
                case SignInStatus.TwoFactorRequired: // said only for a right password
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("Enter the 6-digit code from your authenticator app (or a recovery code) as well.", null, typed)));
                case SignInStatus.TwoFactorSetupRequired: // the shop requires it: set it up now, then they are signed in
                    return Results.Redirect(session.Back("/account/two-factor/setup", new Flash(null, null, new Dictionary<string, string> { ["setupToken"] = result.SetupToken!, ["returnTo"] = ShopSession.SafePath(returnTo, "/account") })));
                case SignInStatus.LockedOut:
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("Too many wrong attempts. Please wait a while, then try again.", null, typed)));
                case SignInStatus.Unverified:
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("Please follow the link we emailed you to confirm your address first.", null, typed)));
                default: // wrong password, no such account, no sign-in or inactive: one answer, so it cannot be used to find out who has an account
                    return Results.Redirect(session.Back("/account/sign-in", new Flash("That email, password and code do not match an account.", null, typed)));
            }
        });

        // Two-factor sign-in, from the account's security page.
        account.MapPost("/two-factor/enable", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerTwoFactorService twoFactor, CancellationToken ct) =>
        {
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return Results.Redirect("/account/sign-in?returnTo=/account/security");
            if (await twoFactor.EnableAsync(shopper.Id, form["code"].ToString(), ct) is not { } codes)
                return Results.Redirect(session.Back("/account/security", new Flash("That code is not right. Check the app and try again."), null) + "&setup=1");
            session.SetRecoveryCodes(codes);
            return Results.Redirect("/account/security");
        });

        account.MapPost("/two-factor/disable", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerTwoFactorService twoFactor, CancellationToken ct) =>
        {
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return Results.Redirect("/account/sign-in?returnTo=/account/security");
            return await twoFactor.DisableAsync(shopper.Id, form["password"].ToString(), form["code"].ToString(), ct)
                ? Results.Redirect(session.Back("/account/security", new Flash(null, "Two-factor sign-in is off.")))
                : Results.Redirect(session.Back("/account/security", new Flash("The password or the code is not right.")));
        });

        account.MapPost("/two-factor/recovery-codes", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerTwoFactorService twoFactor, CancellationToken ct) =>
        {
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return Results.Redirect("/account/sign-in?returnTo=/account/security");
            if (await twoFactor.RegenerateRecoveryCodesAsync(shopper.Id, form["code"].ToString(), ct) is not { } codes)
                return Results.Redirect(session.Back("/account/security", new Flash("That code is not right.")));
            session.SetRecoveryCodes(codes);
            return Results.Redirect("/account/security");
        });

        // When the shop requires two-factor: the shopper has proved their password and holds a setup token. Finishing signs them in.
        account.MapPost("/two-factor/finish", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, ICustomerAccountService accounts, ICustomerTwoFactorService twoFactor, CancellationToken ct) =>
        {
            var setupToken = form["setupToken"].ToString();
            var returnTo = ShopSession.SafePath(form["returnTo"].ToString(), "/account");
            var again = new Flash(null, null, new Dictionary<string, string> { ["setupToken"] = setupToken, ["returnTo"] = returnTo, ["wrong"] = "1" });
            if (await twoFactor.CheckSetupTokenAsync(setupToken, ct) is not { } id)
                return Results.Redirect(session.Back("/account/sign-in", new Flash("That took too long. Please sign in again.")));
            if (await twoFactor.EnableAsync(id, form["code"].ToString(), ct) is not { } codes)
                return Results.Redirect(session.Back("/account/two-factor/setup", again));
            if (await twoFactor.CompleteSetupSignInAsync(setupToken, ct) is not { Status: SignInStatus.Valid } done)
                return Results.Redirect(session.Back("/account/sign-in", new Flash("That took too long. Please sign in again.")));
            session.SetRecoveryCodes(codes);
            return await StartSessionAsync(session, shopBaskets, accounts, done, "/account/security", ct);
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
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back("/account/sign-in", new Flash(Message(e), null, values))); }
            catch (PiranhaCommerceConflictException)
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
            catch (PiranhaCommerceValidationException) { }
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
            catch (PiranhaCommerceValidationException e)
            {
                var back = "/account/reset?email=" + Uri.EscapeDataString(input.Email) + "&token=" + Uri.EscapeDataString(input.Token);
                return Results.Redirect(back + "&f=" + Uri.EscapeDataString(session.Protect(new Flash(Message(e)))));
            }
        });
    }

    private static void MapEngagement(RouteGroupBuilder shop)
    {
        // The wishlist and reviews belong to a signed-in shopper; anyone else is sent to sign in and back.
        static IResult SignInFirst(string back) => Results.Redirect("/account/sign-in?returnTo=" + Uri.EscapeDataString(ShopSession.SafePath(back, "/account")));

        shop.MapPost("/wishlist/add", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IWishlistService wishlists, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return SignInFirst(back);
            try
            {
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaCommerceValidationException("productId", "Choose a product.");
                await wishlists.AddAsync(shopper.Id, productId, ct);
                return Results.Redirect(session.Back(back, new Flash(null, "Saved to your wishlist.")));
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e)))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        shop.MapPost("/wishlist/remove", async (IFormCollection form, ShopBaskets shopBaskets, IWishlistService wishlists, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return SignInFirst(back);
            if (Guid.TryParse(form["productId"], out var productId)) await wishlists.RemoveAsync(shopper.Id, productId, ct);
            return Results.Redirect(ShopSession.SafePath(back, "/account/wishlist"));
        });

        // Photos on a review you have already written. The review keeps what it has; a photo that cannot be taken is explained, never silently dropped.
        shop.MapPost("/reviews/photo", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IReviewPhotoService photos, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return SignInFirst(back);
            if (!Guid.TryParse(form["reviewId"], out var reviewId)) return Results.Redirect(ShopSession.SafePath(back, "/"));
            var problem = await AddPhotosAsync(photos, shopper.Id, reviewId, form.Files, ct);
            return Results.Redirect(session.Back(back, problem is null ? new Flash(null, "Thank you. Your photo will appear once we have looked at it.") : new Flash(problem)) + "#reviews");
        });

        shop.MapPost("/reviews/save", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IReviewService reviews, IReviewPhotoService photos,
            Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return SignInFirst(back);
            var typed = new Dictionary<string, string> { ["rating"] = form["rating"].ToString(), ["title"] = form["title"].ToString().Trim(), ["body"] = form["body"].ToString().Trim() };
            try
            {
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaCommerceValidationException("productId", "Choose a product.");
                if (!int.TryParse(typed["rating"], out var rating) || rating is < 1 or > 5) throw new PiranhaCommerceValidationException("rating", "Choose a rating from 1 to 5 stars.");
                var input = new ReviewInput { ProductId = productId, Rating = rating, Title = Blank(typed["title"]), Body = Blank(typed["body"]) };
                ModelValidator.ValidateOrThrow(input);
                var created = await reviews.CreateAsync(shopper.Id, input, ct);
                var photoProblem = await AddPhotosAsync(photos, shopper.Id, created.Id, form.Files, ct);
                var thanks = options.Value.ReviewsNeedApproval ? "Thank you. Your review will appear once we have read it." : "Thank you. Your review is up.";
                return Results.Redirect(session.Back(back, photoProblem is null ? new Flash(null, thanks) : new Flash(null, thanks + " " + photoProblem)));
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e), null, typed))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        // "Helpful": one tap, a mark that can be taken back. The button says which; the answer is the same page.
        shop.MapPost("/reviews/helpful", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IReviewService reviews, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            if (await shopBaskets.ShopperAsync(ct) is not { } shopper) return SignInFirst(back);
            try
            {
                if (!Guid.TryParse(form["reviewId"], out var reviewId)) throw new PiranhaCommerceValidationException("reviewId", "Choose a review.");
                await reviews.SetHelpfulAsync(shopper.Id, reviewId, form["helpful"] == "true", ct);
                return Results.Redirect(ShopSession.SafePath(back, "/") + "#reviews");
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e)))); }
        });
    }

    private static void MapStockAlerts(RouteGroupBuilder alerts)
    {
        // "Tell me when it is back": anyone may ask, with an email address. The answer never says whether the address was already known.
        alerts.MapPost("/subscribe", async (IFormCollection form, ShopSession session, ShopBaskets shopBaskets, IStockAlertService stockAlerts, CancellationToken ct) =>
        {
            var back = form["returnTo"].ToString();
            var shopper = await shopBaskets.ShopperAsync(ct);
            var email = shopper?.Email ?? form["email"].ToString().Trim();
            try
            {
                if (!Guid.TryParse(form["productId"], out var productId)) throw new PiranhaCommerceValidationException("productId", "Choose a product.");
                await stockAlerts.SubscribeAsync(new StockAlertInput { ProductId = productId, Email = email }, shopper?.Id, ct);
                return Results.Redirect(session.Back(back, new Flash(null, "Thank you. We will email you once, when it is back in stock.")));
            }
            catch (PiranhaCommerceValidationException e) { return Results.Redirect(session.Back(back, new Flash(Message(e), null, new() { ["alertEmail"] = email }))); }
            catch (PiranhaCommerceConflictException e) { return Results.Redirect(session.Back(back, new Flash(e.Message))); }
        });

        // The link in the email opens a page that asks first (mail scanners open links too, so opening one must change nothing); this does the removal.
        alerts.MapPost("/unsubscribe", async (IFormCollection form, ShopSession session, IStockAlertService stockAlerts, CancellationToken ct) =>
        {
            var removed = await stockAlerts.UnsubscribeAsync(form["token"].ToString().Trim(), ct);
            return Results.Redirect(session.Back("/shop", removed
                ? new Flash(null, "Done. We will not email you about that product.")
                : new Flash("That link has already been used, or is not valid.")));
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

    /// <summary>Adds each chosen file as a photo on the shopper's review. Returns what went wrong (for the page to say), or null when all were taken or none were sent.</summary>
    private static async Task<string?> AddPhotosAsync(IReviewPhotoService photos, Guid customerId, Guid reviewId, IFormFileCollection files, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var file in files.Where(f => f.Name == "photos" && f.Length > 0))
        {
            try
            {
                await using var stream = file.OpenReadStream();
                await photos.AddAsync(customerId, reviewId, stream, file.FileName, ct);
            }
            catch (PiranhaCommerceValidationException e) { problems.Add($"\"{Path.GetFileName(file.FileName)}\" was not added: {Message(e)}"); }
        }
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>The service's own words, one sentence per problem.</summary>
    private static string Message(PiranhaCommerceValidationException e) => string.Join(" ", e.Errors.Values.SelectMany(v => v));
}
