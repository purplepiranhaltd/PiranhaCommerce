---
title: Reviews, wishlists and suggestions
order: 80
description: Product reviews, back-in-stock requests, wishlists and related or cross-sell products.
---

This guide covers the features that bring shoppers back and help them buy: reviews, back-in-stock requests, wishlists, and suggested products. The [sample shop](../getting-started/sample-shop.md) has all four, and the [walkthrough](shop-front-walkthrough.md) shows the calls. API paths below are under `/api/v1`.

## Product reviews

| Who | Calls |
|---|---|
| Anyone | `GET /storefront/products/{id}/reviews` and `/storefront/products/{id}/review-summary` |
| A signed-in shopper, for their own reviews | `/storefront/reviews` |
| Staff | `/reviews` (admin: Orders > Product reviews) |

**Writing.** A signed-in shopper rates a product 1 to 5, with an optional title and text. There is one review per shopper per product. When the product has variants the review is kept against the parent, so give any variant's id.

**Approval.** A new or changed review waits for staff approval (`PiranhaCommerce:Ecommerce:ReviewsNeedApproval`, on by default). Only approved reviews are public or counted.

**Names.** Shoppers see a display name: their first name and last initial unless they choose one. Never an email address.

**Verified purchases.** A review is marked verified when the shopper has an order on their account for the product, or any of its variants, that was not cancelled.

**Replies from the shop.** Staff can answer a review (admin: Orders > Product reviews > Reply, or `PUT /reviews/{id}/reply` with a `body` up to 2000 characters; `DELETE /reviews/{id}/reply` removes it). A review has one reply, and a second replaces the first. The reply is recorded with who wrote it and when.
- A reply can be written while the review is waiting, but shoppers see it (as `reply` and `repliedUtc` on the public review) only once the review is approved.
- If the shopper edits their review it goes back for approval, and the reply is kept for when it returns. Remove it if it no longer fits.
- A reply is plain text, like a review, so the shop front must escape it. It is exported with the reviewer's data and goes with the review when it is erased.
- **The reviewer is emailed** the first time the shop replies (template `review.replied`, with `displayName`, `productName`, `productSlug`, `productId`, `rating`, `reviewTitle`, `reply` and `published`; `published` is false while the review still waits for approval, so the email can say the reply will appear later). Changing the reply does not send it again, so fixing a typo does not email them twice; removing it and replying afresh does. Switch it off with `PiranhaCommerce:Ecommerce:SendReviewReplies` (default on). It needs an email package, and a failed send never stops the reply being saved.
- Not built: public replies from anyone but the shop.

**"Was this helpful".** A signed-in shopper can mark someone else's review helpful (`PUT /storefront/reviews/{id}/helpful`) and take the mark back (`DELETE` on the same address).
- A shopper has at most one mark per review; marking twice, or taking back a mark they never made, changes nothing and still succeeds.
- Only reviews that shoppers can see can be marked: approved, on a product that is on sale. Anything else is `404`. Marking your own review is refused (`400`), and a guest gets `401`.
- Each public review carries `helpfulCount` and `votedByMe`. `votedByMe` is only ever true for the signed-in shopper asking: send the token with `GET /storefront/products/{id}/reviews`. Nobody can see who else marked a review.
- A shopper's marks are exported with their data and deleted when they are erased, so the count falls. Deleting a review deletes its marks.
**Photos.** A signed-in shopper can attach pictures to their own review.
- `POST /storefront/reviews/{id}/photos` takes `multipart/form-data` with a `file`. The type is decided from the file's own bytes (JPEG, PNG, WebP or GIF; SVG and anything else is refused, whatever the name or content type says), and it can be up to `PiranhaCommerce:Images:MaxBytes` (10 MB). A review can have up to `PiranhaCommerce:Ecommerce:MaxReviewPhotos` photos (4; 0 switches photos off).
- With an image processor installed (the sample uses SkiaSharp) the photo is **re-made, not stored as sent**: turned upright, kept to 2000 px on its longest side, and the smaller sizes of `PiranhaCommerce:Images:Sizes` are made beside it. Re-making drops what a phone writes into a photo, which often includes where it was taken. Without a processor the file is stored as it came, so install one if shoppers upload.
- A photo is **only reachable while its review is approved** and its product is on sale. Its address (`/storefront/guest/review-photos/{id}`, with `?width=` for a smaller size) answers `404` before that and again after a rejection, unlike product pictures, because this is something a shopper sent that no one has read yet. The writer can fetch their own at `GET /storefront/reviews/{id}/photos/{photoId}` and staff any photo at `GET /reviews/{id}/photos/{photoId}`, so staff can look before approving.
- Public reviews, and a shopper's own and staff's review lists, carry `photos` (each an `id`, `url`, `width` and `height`).
- Adding a photo sends the review back to waiting when approval is on, like changing its text. Taking one off (`DELETE /storefront/reviews/{id}/photos/{photoId}`) does not. Staff can take one off without rejecting the review: `DELETE /reviews/{id}/photos/{photoId}`, or Remove photo on the admin Product reviews page, which shows a small preview of each.
- Photos are exported with the shopper's data (the file names and sizes, not the pictures) and the files are deleted when they are erased. Deleting a review, or a photo, deletes its files too.
- Not built: a "not helpful" vote, and sorting reviews by how helpful they are.

**Escaping.** Text is stored as plain text, so the shop front must escape it.

**Privacy.** Reviews are exported with a customer and deleted when the customer is erased.

## Back-in-stock requests

A shopper asks to be emailed when a sold-out product can be bought again. It works for a simple product or one variant, not a parent that has options.

| Who | Calls |
|---|---|
| Guests and shoppers | `POST /storefront/stock-alerts` with a product and an email; `DELETE /storefront/stock-alerts/{token}` to unsubscribe |
| Staff | `/stock-alerts` and `/stock-alerts/demand` (admin: Orders > Back-in-stock requests) |

**Rules**
- Asking again is a silent success, in any letter case, so the answer never says whether an address was already known.
- An address can wait for at most 25 products.
- Asking for something that can be bought now is refused.

**Sending.** A background check emails each waiting person once the product is published and has stock at the location online orders take from (`StockLocationId`).

- It runs every `PiranhaCommerce:Ecommerce:StockAlertPollSeconds` (default 300; `0` turns it off), or call `IStockAlertNotifier.RunOnceAsync` from your own scheduler.
- Each request is claimed before sending, so several running copies never both send.
- A failed send is retried after ten minutes.

**The email** is template `stock.back-in-stock` with the variables `productName`, `sku`, `productId`, `unsubscribeToken` and `unsubscribeUrl`. The URL is built from `PiranhaCommerce:Ecommerce:StockAlertUnsubscribeUrlTemplate`, with `{token}` in it. Make that link open a page that asks first and removes the request when the shopper presses a button: mail scanners open links, so opening one must change nothing.

**Privacy.** Requests are exported with a customer and deleted when they are erased, matched by account or by email address.

## Wishlists

A signed-in shopper saves products (a simple product or one chosen variant) to come back to.

- `GET|POST /storefront/wishlist` lists and adds. `DELETE /storefront/wishlist/{productId}` removes one, and `DELETE /storefront/wishlist` empties it.
- Staff read `/wishlists/demand` (admin: Orders > Wishlists) to see what people are saving.

**Rules.** Only products on sale can be saved. Saving one already saved is a quiet success. A list holds up to 200.

**What an item shows**, so a shopper sees changes:

- the price this shopper pays now (a sale or group price included, as on the product page), the regular price, and the price when it was saved,
- its `slug` and main picture,
- whether it is still on sale (`published`), and
- whether it can be bought now (`available`: on sale and in stock where online orders take it from).

A list is private to its owner, exported with the customer and deleted when they are erased.

## Related and cross-sell products

Staff choose two lists for each product, in order and at most 20 each:

- **related** products, suggested on the product's page, and
- **cross-sell** products, suggested when it is in the basket.

Staff use `GET|PUT /products/{id}/relations` (admin: the "Related and cross-sell products" panel on a product). Shoppers use `GET /storefront/products/{id}/suggestions` and `GET /storefront/cross-sell?productIds=...`.

**Rules for staff**
- A `PUT` replaces both lists.
- A product cannot be suggested for itself, twice in a list, or as a variant. Suggestions belong to the main product, and a variant's page shows its parent's.
- A suggestion goes one way only.

**What shoppers see.** Only products on sale, each with the price the shopper pays (and the regular price), its `slug` and main picture, and whether it can be bought now (`available`).

**Suggestions for a basket.** The cross-sell call takes the product ids in the basket (the shop front knows them, so guest and signed-in baskets work alike). It returns the cross-sell products of all of them that are on sale, leaves out anything already in the basket, puts those suggested by the most products first, and returns at most 12.
