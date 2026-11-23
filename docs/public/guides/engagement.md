---
title: Reviews, wishlists and suggestions
order: 80
description: Product reviews, back-in-stock requests, wishlists and related or cross-sell products.
---

## Product reviews

(`/storefront/products/{id}/reviews` and `/review-summary` for anyone; `/storefront/reviews` for the signed-in shopper's own; `/reviews` for staff, admin: Orders > Product
reviews): a signed-in shopper rates a product 1 to 5 with an optional title and text, one review per shopper per product, kept against the parent when the product has variants (give any variant's id).
A new or changed review waits for staff approval (`Piranha:Ecommerce:ReviewsNeedApproval`, on by default); only approved ones are public or counted, and shoppers see a display name (their
first name and last initial unless they choose one), never an email. A review is marked verified when the shopper has an order on their account for the product or any of its variants that was not
cancelled. Text is stored as plain text, so the shop front must escape it. Reviews are exported with a customer and deleted when the customer is erased.

## Back-in-stock requests

(`POST /storefront/stock-alerts` with a product and an email, open to guests and shoppers; `DELETE /storefront/stock-alerts/{token}` to unsubscribe; staff read
`/stock-alerts` and `/stock-alerts/demand`, admin: Orders > Back-in-stock requests): a shopper asks to be emailed when a sold-out product can be bought again, for a simple product or one variant
(not a parent that has options). Asking again is a silent success, in any letter case; an address can wait for at most 25 products; asking for something in stock now is refused. A background check
(`Piranha:Ecommerce:StockAlertPollSeconds`, default 300, 0 off; or call `IStockAlertNotifier.RunOnceAsync`) emails each waiting person once the product is published and has stock at the
location online orders take from (`StockLocationId`), claiming each request first so several running copies never both send; a failed send is retried after ten minutes. The email is template
`stock.back-in-stock` with `productName`, `sku`, `productId`, `unsubscribeToken` and `unsubscribeUrl` (from `StockAlertUnsubscribeUrlTemplate`, with `{token}` in it). Requests are exported with a
customer and deleted when they are erased, matched by account or by email address.

## Wishlists

(`GET/POST /storefront/wishlist`, `DELETE /storefront/wishlist/{productId}` and `DELETE /storefront/wishlist` to empty it; staff read `/wishlists/demand`, admin: Orders > Wishlists):
a signed-in shopper saves products (a simple product or one chosen variant) to come back to. Only products on sale can be saved; saving one already saved is a quiet success; a list holds up to 200.
Each item shows the price this shopper pays now (a sale or group price included, as on the product page), the regular price, the price when it was saved, its `slug` and main picture, whether it is still on sale (`published`) and whether it can be bought now (`available`: on sale and in stock where online orders take
it from), so a shopper sees changes. A list is private to its owner, exported with the customer and deleted when they are erased.

## Related and cross-sell products

(staff `GET/PUT /products/{id}/relations`, admin: the "Related and cross-sell products" panel on a product; shoppers `GET /storefront/products/{id}/suggestions`
and `GET /storefront/cross-sell?productIds=...`): staff choose, in order and at most 20 each, products to suggest on a product's page (related) and products to suggest when it is in the basket
(cross-sell). A PUT replaces both lists; a product cannot be suggested for itself, twice in a list, or as a variant (suggestions belong to the main product, and a variant's page shows its parent's).
A suggestion goes one way only. Shoppers see only products on sale, each with the price the shopper pays (and the regular price), its `slug` and main picture, and whether it can be bought now (`available`). The basket call takes the product ids in the basket (the shop
front knows them, so guest and signed-in baskets work alike) and returns the cross-sell products of all of them that are on sale, leaving out anything already there, those suggested by the most
products first, at most 12.
