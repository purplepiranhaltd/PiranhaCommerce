---
title: Walkthrough - a shop front, page by page
order: 6
description: The pages a shop front needs, the calls behind each one, and how the Petalwick sample does it.
---

This page follows a shopper through a shop and says, for each step, what to call. Every step has two forms: the **API** (any front end, over HTTP) and the **service** (a shop front inside the same application can call it directly, as the [sample shop](../getting-started/sample-shop.md) does). Read the sample's code next to this page: the files named below are in `samples/PiranhaCommerce.Web/Shop`.

Paths below start at `/api/v1/storefront`.

## 1. Browse

| Page | API | Service |
|---|---|---|
| Menu | `GET /categories` | `IShopCatalogueService` |
| Listing and search | `GET /products?category=...&q=...&sort=...&inStock=true&brand=...&spec=finish:matte&tag=summer&page=2` | `IShopCatalogueService` |
| Filter checkboxes with counts | `GET /products/facets` (same query as the listing) | `IShopCatalogueService` |
| Product | `GET /products/by-slug/{slug}` | `IShopCatalogueService` |
| "You may also like" | `GET /products/{id}/suggestions` | `IProductRelationService` |

Show `price` for the shopper, `regularPrice` struck through when `discounted` is true, and the tax-inclusive figures when you sell to consumers. For a product with options (shade, size) show the picker from `options` and `variants`, and add the **variant's** `productId` to the basket, never the parent's. Sold-out variants stay in the picker: mark them, do not hide them. Details are in [Building a shop front](storefront.md). In the sample: `Pages/Listing.razor`, `Pages/Product.razor`.

## 2. The basket

A basket is created on the first "Add to basket" and its id is kept in the shopper's browser (the sample uses an HttpOnly cookie, so a script on the page cannot read it).

| Step | Guest | Signed in |
|---|---|---|
| Start | `POST /guest/baskets` | `POST /baskets`, or `GET /baskets/current` for the one they already have |
| Add or change a line | `PUT /guest/baskets/{id}/lines/{productId}` with a quantity | `PUT /baskets/{id}/lines/{productId}` |
| Remove | `DELETE .../lines/{productId}` | the same |
| Promo code | `POST .../promo` | the same |
| Show it | `GET /guest/baskets/{id}` | `GET /baskets/{id}` |

Check stock when a line is added, and again at checkout: the basket does not reserve stock, and checkout refuses what has sold out in the meantime. Show a "Goes well with" row from `GET /cross-sell?productIds=...`. See [Baskets and orders](baskets-orders.md). In the sample: `Pages/Basket.razor`, `ShopBaskets.cs`.

## 3. Checkout

1. Ask for an email address and a delivery address.
2. `GET .../shipping-options?countryCode=GB&postalCode=...` lists what can be chosen, with prices.
3. `POST .../quote` with the chosen method gives the totals to show: goods, discount, delivery, tax and the total. The quote is what is charged.
4. `POST .../checkout` places the order: email, addresses, `shippingMethodId` and an optional note. It is one transaction, and stock is taken in it. The answer is the order with its reference.

If the store takes card payments, start the payment next (`POST /orders/{id}/pay`) and send the shopper to the gateway; see [Payments](payments.md). Without one the order simply waits for payment, which is what the sample does. In the sample: `Pages/CheckoutAddress.razor`, `Pages/CheckoutDelivery.razor`, `Pages/Confirmed.razor`.

Whatever your front end is, do not trust it with who is ordering: a signed-in shopper's customer id comes from their sign-in, never from a form field. The sample sets it from the signed-in shopper and ignores anything posted.

## 4. Accounts

| Step | API |
|---|---|
| Register | `POST /auth/register` |
| Sign in | `POST /auth/token`, which returns an access token and a refresh token |
| Keep signed in | `POST /auth/refresh` |
| Forgot password | `POST /auth/forgot-password`, then `POST /auth/reset-password` with the emailed code |
| Their details and addresses | `GET|PUT /me`, `/me/addresses` |
| Their orders | `GET /orders`, `GET /orders/{id}` |
| Change password, sign out everywhere | `POST /me/password`, `POST /me/sign-out-everywhere` |

When a shopper signs in with a guest basket, claim it (`POST /baskets/{id}/claim`), or if they already had one, merge it in (`POST /baskets/{id}/merge`), so nothing they chose is lost. Give sign-in one vague failure message and a rate limit. See [Customers and shopper accounts](shoppers.md).

A shop front inside the application can use its own cookie for sign-in instead of tokens. The sample does: a separate cookie scheme that is never the default, whose every request is checked against the customer's current session version, so changing a password or deactivating a customer ends every session at once. See `ShopAuth.cs`.

## 5. Engagement

| Feature | API |
|---|---|
| Reviews on a product page | `GET /products/{id}/reviews` for everyone; `GET|POST /reviews` (and `PUT|DELETE /reviews/{id}`) for a signed-in shopper's own. Each public review can carry the shop's `reply`, and has `helpfulCount` and `votedByMe`; `PUT|DELETE /reviews/{id}/helpful` marks and unmarks |
| Wishlist | `GET /wishlist`, `POST /wishlist`, `DELETE /wishlist/{productId}` |
| "Tell me when it is back" | `POST /stock-alerts`, anyone, with an email address; `DELETE /stock-alerts/{token}` for the unsubscribe link |

See [Reviews, wishlists and suggestions](engagement.md).

## Things every shop front gets wrong once

- **Prices from your own sums.** Show the prices the API gives, not ones you worked out. Totals especially: use the quote.
- **Trusting hidden fields.** Quantities, prices and customer ids from a form are requests, not facts. The services check them.
- **Showing who has an account.** "No account with that email" on sign-in or reset tells a stranger who your customers are.
- **Links that change things.** Anything an email scanner might open (unsubscribe links, confirmations) should show a page with a button, as the sample does.
- **No limit on forms that send email.** Registration, password reset and back-in-stock sign-up each send mail to an address the shopper typed.
