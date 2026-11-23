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
| Listing | `/shop`, `/shop/{category}` | A category with its sub-categories, sorted, filtered to what is in stock, paged |
| Search | `/search?q=...` | Products matching every word |
| Product | `/product/{slug}` | Pictures, price with VAT, sale and quantity prices, a picker for shades or sizes (`?variant=...`), stock, reviews total, suggestions and an Add to basket form |
| Basket | `/basket` | Lines with their prices (VAT and any code included), quantity changes, removal and a promo code |
| Checkout | `/checkout`, `/checkout/delivery` | A guest's email and address, then the delivery methods that serve that address, each with the order total it gives, then Place order |
| Confirmation | `/order/confirmed` | The order just placed, with its reference, lines, delivery, VAT and total |

Each page gets its data from the same services the storefront API uses (`IShopCatalogueService`, `IProductRelationService`, `IGroupPricingService`), so what the shop shows is exactly what [the API returns](../guides/storefront.md). Prices are shown with VAT for the country in `Piranha:Shop:CountryCode` (default `GB`). Accounts (sign-in, order history, wishlist, reviews) are the next part of the shop front and are not built yet, so today the shop takes guest orders only. No payment is taken: an order is placed unpaid and appears in the admin. Set up [payments](../guides/payments.md) to take cards.

## How the shop front works

It shows one way to build a shop front on the packages. Take what suits you.

- **Data comes from services, in process.** The pages call the shop services directly. They never go through the HTTP API, so there are no tokens to manage. A small class (`ShopFront`) runs these calls one at a time, because a page and its layout load at the same moment and share one database connection for the request.
- **Forms post, then redirect.** Every button is an ordinary HTML form that posts to `/basket/...` or `/checkout/...`, does its work and redirects to a page, so a refresh never repeats an action. A problem goes back to the page as a short message in the address (`?f=`), which is signed and expires after ten minutes, so it cannot be forged. No JavaScript is needed.
- **Every post carries an antiforgery token** and is refused without it. Posts are also rate limited per client address (`Piranha:Shop:FormsPerMinute`, 120 by default) and answer 429 beyond that, because starting a basket costs a database row. Behind a proxy, configure forwarded headers so the address is the visitor's.
- **The basket belongs to a cookie.** The first Add to basket starts a basket and puts its id in an HttpOnly cookie. Knowing the id is the whole proof for a guest basket, so on every use the shop checks that the basket still exists, is open and has no owner. A cookie holding any other basket's id, including a signed-in customer's, is ignored and the shopper gets a new basket. Looking at pages never creates a basket.
- **Checkout keeps its details in an encrypted cookie** between the two steps, and the confirmation page reads the order from another one. The order is never loaded from an id in the address, so nobody can read someone else's order by guessing.
- **Guest orders belong to no customer.** The shop sends no customer id, whatever the form says. `Piranha:Ecommerce:AllowGuestCheckout` switches guest orders off.
- **Stock is checked twice.** The add form refuses a sold-out shade or more than are left, with a reason. Checkout checks again, so stock that sold in between is caught with a message and no order is made.

## The sample data

The sample host can fill an empty shop with a small, invented cosmetics catalogue so you have something real to browse, price and buy. The brand, products and reviews are made up for the sample. The pictures are drawn when the data is created, so there are no image files and no one's photographs.

## Switching it on

The data is created at startup when `Piranha:SampleData:Enabled` is `true`. It is on in `appsettings.Development.json` and off everywhere else, and it only ever runs when the host is in the Development environment. Running it again adds nothing twice: each category, product, zone, method, code, sale and shopper is looked up first and left alone if it is there.

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
