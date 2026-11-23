---
title: Baskets and orders
order: 30
description: Baskets, guest checkout, merging, checkout, order stages and housekeeping.
---

## Baskets and orders

`PiranhaCommerce.Ecommerce` turns the shared catalogue into an online shop. API (under `/api/v1`):

- `POST /baskets`, `GET /baskets/{id}`, `PUT|DELETE /baskets/{id}/lines/{productId}` - only products published through the product's
  online listing can be added; prices are always read live from the catalogue.
- `POST /baskets/{id}/checkout` - email, shipping address (billing defaults to it), optional customer link and note. The order, a copy
  of every name, price and address, and the stock taken from the shelf are committed in **one transaction**: short stock or an
  unpublished product refuses the whole checkout (409) and nothing changes. Checking out the same basket again returns the same order.
- `GET /orders` (stage and search filters), `GET /orders/{id}`, `GET /orders/by-number/{number}`,
  `POST /orders/{id}/stage` - Placed to Processing or Cancelled, Processing to Completed or Cancelled. Cancelling puts stock back once.

Order numbers are sequential (first `ORD-1001`). Orders never change when a product is edited, and survive deleting a customer; a
product that has been sold cannot be deleted. Settings: `Piranha:Ecommerce:Currency` (default `GBP`) and
`Piranha:Ecommerce:StockLocationId` (default: the built-in location). Enums travel as names in the API (`"Placed"`).
See the other guides for shipping, tax, promotions and payments.

## Guest baskets and checkout

(anonymous, under `/api/v1/storefront/guest/baskets`): a shopper who is not signed in can `POST` a basket, add and remove
lines (`PUT|DELETE /{id}/lines/{productId}`), use a promo code or gift voucher, see shipping options and a quote, and check out with an email address and
shipping address (`POST /{id}/checkout`, same body as the signed-in checkout; any `customerId` in it is ignored). The order belongs to no customer, the
confirmation goes to the email given, and group prices and group-limited promo codes do not apply (nobody is identified). Knowing the basket id is the
whole of the proof, as it is for claiming a basket; these routes only ever touch a basket that has no owner, so a signed-in shopper's basket answers
`404` here, and the signed-in routes still need a token. Starting a guest basket is limited per client address (`Piranha:RateLimit:GuestBasketAttempts`,
default 120 a minute: a shop front on one server address makes one per visitor), and promo and voucher codes share the sign-in limit. Set
`Piranha:Ecommerce:AllowGuestCheckout` to `false` to require an account to check out (guests can still build a basket; checkout answers `403`). There is
no guest order lookup afterwards: the order comes back from the checkout call and by email. Old unowned baskets are cleared like any (`AbandonedBasketDays`).

## Finding a shopper's basket again

`GET /storefront/baskets/current`, signed in, returns the open basket the shopper used most recently, or `404` when they have none. A basket is otherwise known only by its id, so this is how one follows a shopper to a new device or back after signing out. The sample shop uses it when someone signs in: it claims or merges the basket they were using as a guest, or picks up the one they left.

## Merging baskets

`POST /storefront/baskets/{id}/merge` with `{ "fromBasketId" }`, signed in, moves the lines of a guest basket into the shopper's own
basket and deletes the guest one. Quantities add up (a line never goes past 10,000), a product that can no longer be bought is left behind, and a promo code
or voucher on the guest basket comes along only when the shopper's basket has none (and a code limited to a group only if they are in it). Anything
that is not their own basket or not an open guest basket is `404`, and two merges of the same guest basket at once move its lines once. Use `claim` instead
when the shopper has no basket yet and the whole guest basket should simply become theirs.

## Abandoned baskets

open baskets nobody has touched for `Piranha:Ecommerce:AbandonedBasketDays` (default 60; 0 keeps them for ever) are deleted with
their lines by a background job that runs every six hours (first run a minute after start). Any change to a basket (a line, a promo code, a voucher, a
shopper claiming it) counts as being active. Checked-out baskets are never touched. Baskets hold no stock, so this is housekeeping only. `IBasketHousekeeper`
does the work if you want to run it from your own scheduler.

## Unpaid orders

(off by default): with `Piranha:Ecommerce:UnpaidOrderHours` above 0, an order still at Placed with no payment of any kind after that many
hours is cancelled by a background job (every 15 minutes), exactly as if staff had cancelled it: the stock goes back on the shelf, the promo use is given
back and the buyer gets the cancellation email. The movement is recorded as done by "system (unpaid order expired)". Part-paid orders, orders already in
progress and orders with nothing to pay are never touched, and a payment that lands a moment before is detected inside the cancel and wins. A payment
that arrives after is refused like any payment on a cancelled order (a gateway one lands on the unreconciled list). Leave it off if you take orders on
account or record payments by hand later. `IUnpaidOrderExpiry` does the work if you want to run it from your own scheduler.
