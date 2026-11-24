---
title: The sample shop
order: 5
description: The sample shop front, and the invented cosmetics catalogue the sample host fills itself with.
---

The sample host (`samples/PiranhaCommerce.Web`) is a working shop as well as an admin and an API: run it and open `http://localhost:5280/`. The shop front is ordinary server-rendered Blazor pages in the `Shop` folder (no JavaScript is needed), and the admin is at `/admin` (the footer links to it).

## What the shop front does today

| Page | Address | Shows |
|---|---|---|
| Home | `/` | Categories, what is on sale, newest products |
| Listing | `/shop`, `/shop/{category}` | A category with its sub-categories, sorted, filtered to what is in stock, by brand and by specification such as finish, coverage, skin type or vegan (checkboxes with counts, no JavaScript), paged. A "Browse by tag" row of links (`/shop?tag=bestseller`) shows the tags that have products, the page for a single tag is headed "Tagged: Bestseller", and each product page links to its tags. The search box finds tag names too. A single brand (`/shop?brand=petalwick`) gets its own heading and description |
| Search | `/search?q=...` | Products matching every word |
| Product | `/product/{slug}` | Pictures, price with VAT, sale and quantity prices, a picker for shades or sizes (`?variant=...`), stock, reviews and a form to write one, a wishlist button, "Tell me when it is back" on a sold-out shade, suggestions and an Add to basket form |
| Basket | `/basket` | Lines with their prices (VAT and any code included), quantity changes, removal and a promo code |
| Checkout | `/checkout`, `/checkout/delivery` | A guest's email and address, then the delivery methods that serve that address, each with the order total it gives, then Place order |
| Sign in | `/account/sign-in` | Sign in, or create an account (the basket comes with you) |
| Password | `/account/forgot`, `/account/reset` | Ask for an emailed link, then choose a new password |
| Account | `/account`, `/account/orders/{id}` | The shopper's details, their orders and each order in full |
| Wishlist | `/account/wishlist` | Saved products, with a note when one is sold out, withdrawn or has changed price |
| Stop emails | `/stock-alert/unsubscribe?token=...` | The link in a back-in-stock email; it asks before it removes the request |
| Confirmation | `/order/confirmed` | The order just placed, with its reference, lines, delivery, VAT and total |

Each page gets its data from the same services the storefront API uses (`IShopCatalogueService`, `IProductRelationService`, `IGroupPricingService`), so what the shop shows is exactly what [the API returns](../guides/storefront.md). Prices are shown with VAT for the country in `PiranhaCommerce:Shop:CountryCode` (default `GB`). No payment is taken: an order is placed unpaid and appears in the admin. Set up [payments](../guides/payments.md) to take cards.

## How the shop front works

### Wishlist, reviews, back-in-stock and basket suggestions

- **Wishlist and reviews need a sign-in.** A guest who presses the button is sent to sign in and returned to the product. Saving a product twice keeps one.
- **Reviews wait for approval** when `ReviewsNeedApproval` is on (the default): the writer sees their own review at once, marked as waiting, and nobody else does until staff approve it in the admin. Each shopper can review a product once. A reply from the shop (written in the admin) shows under an approved review, as plain text. The review form takes up to four photos (and a shopper can add more to their review afterwards); they show under the review once staff approve it. A signed-in shopper sees a "Helpful (n)" button on other people's reviews (it becomes "Marked helpful" once tapped, and tapping again takes it back); everyone else sees the count. The name shown is a first name and the first letter of the surname, never the email address. A review from someone who bought the product is marked "Verified purchase".
- **Back-in-stock requests need no account**, only an email address, and the answer is the same whether or not that address was already waiting. They are on the strict account-form limit because the form sends email. The notifier emails once; its unsubscribe link needs `PiranhaCommerce:Ecommerce:StockAlertUnsubscribeUrlTemplate` to point at `/stock-alert/unsubscribe?token={token}` (the Development settings do). The page asks before removing the request, because mail scanners open links.
- **"Goes well with"** on the basket comes from the cross-sell suggestions staff set on each product. It never offers something already in the basket, and prices are the shopper's own, with VAT.

It shows one way to build a shop front on the packages. Take what suits you.

- **Data comes from services, in process.** The pages call the shop services directly. They never go through the HTTP API, so there are no tokens to manage. A small class (`ShopFront`) runs these calls one at a time, because a page and its layout load at the same moment and share one database connection for the request.
- **Forms post, then redirect.** Every button is an ordinary HTML form that posts to `/basket/...` or `/checkout/...`, does its work and redirects to a page, so a refresh never repeats an action. A problem goes back to the page as a short message in the address (`?f=`), which is signed and expires after ten minutes, so it cannot be forged. No JavaScript is needed.
- **Every post carries an antiforgery token** and is refused without it. Posts are also rate limited per client address (`PiranhaCommerce:Shop:FormsPerMinute`, 120 by default) and answer 429 beyond that, because starting a basket costs a database row. Behind a proxy, configure forwarded headers so the address is the visitor's.
- **The basket belongs to a cookie.** The first Add to basket starts a basket and puts its id in an HttpOnly cookie. Knowing the id is the whole proof for a guest basket, so on every use the shop checks that the basket still exists, is open and has no owner. A cookie holding any other basket's id, including a signed-in customer's, is ignored and the shopper gets a new basket. Looking at pages never creates a basket.
- **Checkout keeps its details in an encrypted cookie** between the two steps, and the confirmation page reads the order from another one. The order is never loaded from an id in the address, so nobody can read someone else's order by guessing.
- **Guest orders belong to no customer.** The shop sends no customer id, whatever the form says. `PiranhaCommerce:Ecommerce:AllowGuestCheckout` switches guest orders off, and checkout then asks for a sign-in.
- **Shoppers sign in with a cookie of their own** (`.Petalwick.Shopper`), separate from the admin's, so a shopper can never be taken for staff. It is never the default scheme and only the shop's own code reads it, asking for it by name. It holds the customer's id and the version of their sign-in. On every request the version is checked, so changing a password, or deactivating the customer, ends every shop session at once.
- **The basket follows the shopper.** On sign-in a guest basket is claimed, or added to the basket they already had (quantities add up), and a shopper with no basket cookie on a new browser gets the open basket they used last (`GET /storefront/baskets/current`). On sign-out the basket leaves the browser. Group prices apply from the moment of sign-in, on product pages as well as in the basket.
- **A signed-in order is the customer's own,** with their account's email whatever the form says. Order history uses the customer-scoped calls, so another shopper's order is a 404.
- **Sign-in says one thing.** A wrong password, an unknown email, an account with no sign-in and a deactivated customer all give "That email and password do not match an account", so the form cannot be used to find out who has an account. Repeated wrong passwords lock the account (`PiranhaCommerce:Customers`), and the sign-in, registration and password forms are limited to `PiranhaCommerce:Shop:AccountFormsPerMinute` (10) per client address, far below the general limit. Registering with an email that already belongs to a customer, perhaps from an earlier guest order, says to use "Forgot your password?" rather than saying whether it has a password; the emailed link is also how such a customer gets a sign-in.
- **Password reset emails** need `PiranhaCommerce:Customers:ResetUrlTemplate` to point at `/account/reset` (the Development settings do) and a configured email sender (see [Email](../guides/email.md)). When the email cannot be sent a warning is logged and the shopper sees the same message as ever, so run the sample with an SMTP catcher (such as a local mail trap) to try the reset by hand.
- **Stock is checked twice.** The add form refuses a sold-out shade or more than are left, with a reason. Checkout checks again, so stock that sold in between is caught with a message and no order is made.

## The sample data

The sample host can fill an empty shop with a small, invented cosmetics catalogue so you have something real to browse, price and buy. The brand, products and reviews are made up for the sample. The pictures are drawn when the data is created, so there are no image files and no one's photographs.

## Switching it on

The data is created at startup when `PiranhaCommerce:SampleData:Enabled` is `true`. It is on in `appsettings.Development.json` and off everywhere else, and it only ever runs when the host is in the Development environment. Running it again adds nothing twice: each category, product, zone, method, code, sale and shopper is looked up first and left alone if it is there.

To start again from nothing, drop the development database (`dotnet ef database drop` in the sample project) and run the host.

## What you get

- **Categories:** Lips (lipsticks, lip gloss, lip liners), Eyes (mascara, eyeshadow palettes), Face (foundation, blush and highlighter), Skin (cleansers, moisturisers, serums and protection) and Gift sets.
- **Sixteen products** with prices, descriptions, weights, page-title text and pictures. Lipsticks, gloss, liners, mascara, foundation, blush and highlighter come in **shades**, each a variant with its own picture and stock. The moisturiser comes in two **sizes** at different prices.
- **Stock to demonstrate with:** most shades are well stocked, a few are low (the "only 3 left" message), and some are sold out, so you can try the back-in-stock request.
- **Delivery and tax:** a United Kingdom zone and a rest-of-the-world zone, three delivery methods priced by zone (standard delivery is free over 40.00), and VAT at 20% for the UK.
- **Discounts:** the promo code `WELCOME10` (10% off orders of 20.00 or more), a 20% sale on everything in Eyes, and a quantity price on lip liners (15% off for three or more).
- **Suggestions:** related and cross-sell products for the lip, face and skin ranges.
- **Shoppers and reviews:** three sample accounts, each with the password `Demo-Passw0rd!`, and a handful of approved reviews. The addresses are `demo@example.com`, `maya@example.com` and `jess@example.com`. This password is for local development only.

## Using it in your own tests

The seeder is an ordinary class, `SampleDataSeeder`, registered in the sample host. The integration tests turn the switch off, so the shared test database stays empty, and call the seeder directly in the two tests that need a populated shop. They then use the same storefront API a shop front uses: [Building a shop front](../guides/storefront.md).
