---
title: Baskets and orders
order: 30
description: Baskets, guest checkout, merging, checkout, order stages and housekeeping.
---

`PiranhaCommerce.Ecommerce` turns the shared catalogue into an online shop. This guide covers the basket and checkout, orders and their stages, guest shoppers, and the background jobs that tidy up. Shipping, tax, promotions and payments have their own guides: [Shipping, returns and refunds](shipping.md), [Prices, tax and discounts](pricing.md) and [Payments](payments.md).

API paths below are under `/api/v1`. Enums travel as names (`"Placed"`).

## Baskets and checkout

- `POST /baskets`, `GET /baskets/{id}` and `PUT|DELETE /baskets/{id}/lines/{productId}` build a basket. Only products published through the product's online listing can be added, and prices are always read live from the catalogue.
- `POST /baskets/{id}/checkout` places the order: an email, a shipping address (billing defaults to it), an optional customer link and a note.

**Checkout is one transaction.** The order, a copy of every name, price and address, and the stock taken from the shelf are committed together. Short stock or an unpublished product refuses the whole checkout (`409`) and nothing changes. Checking out the same basket again returns the same order.

**Baskets hold no stock.** Stock is taken only at checkout, so a basket can hold more than is left. Check availability when a line is added, and expect checkout to refuse what has sold out in the meantime.

## Orders

- `GET /orders` (with stage and search filters), `GET /orders/{id}` and `GET /orders/by-number/{number}` read orders.
- `POST /orders/{id}/stage` moves an order on: Placed to Processing or Cancelled, and Processing to Completed or Cancelled. Cancelling puts the stock back once.

Order numbers are sequential, starting at `ORD-1001`. Orders never change when a product is edited, and they survive deleting a customer. A product that has been sold cannot be deleted.

**Settings:** `PiranhaCommerce:Ecommerce:Currency` (default `GBP`) and `PiranhaCommerce:Ecommerce:StockLocationId` (default: the built-in location).

## Guest baskets and checkout

A shopper who is not signed in uses the anonymous routes under `/api/v1/storefront/guest/baskets`. They can:

- `POST` a basket, and add and remove lines (`PUT|DELETE /{id}/lines/{productId}`),
- use a promo code or gift voucher,
- see shipping options and a quote, and
- check out with an email address and shipping address (`POST /{id}/checkout`, the same body as the signed-in checkout; any `customerId` in it is ignored).

**What a guest order is.** It belongs to no customer, and the confirmation goes to the email given. Group prices and group-limited promo codes do not apply, because nobody is identified. There is no guest order lookup afterwards: the order comes back from the checkout call and by email.

**How it is protected.**
- Knowing the basket id is the whole of the proof, as it is for claiming a basket.
- These routes only ever touch a basket that has no owner, so a signed-in shopper's basket answers `404` here, and the signed-in routes still need a token.
- Starting a guest basket is limited per client address (`PiranhaCommerce:RateLimit:GuestBasketAttempts`, default 120 a minute). A shop front on one server address makes one per visitor, so the limit counts that address's visitors together.
- Promo and voucher codes share the sign-in limit.

**Requiring an account.** Set `PiranhaCommerce:Ecommerce:AllowGuestCheckout` to `false`. Guests can still build a basket, but checkout answers `403`.

Old unowned baskets are cleared like any other (see below).

## Signed-in shoppers and their baskets

### Finding a shopper's basket again

`GET /storefront/baskets/current`, signed in, returns the open basket the shopper used most recently, or `404` when they have none. A basket is otherwise known only by its id, so this is how one follows a shopper to a new device, or back after signing out.

The sample shop uses it when someone signs in: it claims or merges the basket they were using as a guest, or picks up the one they left.

### Claiming and merging

- **Claim** (`POST /storefront/baskets/{id}/claim`) makes an unowned basket the shopper's. Use it when the shopper has no basket yet. Two shoppers claiming at once leaves one owner.
- **Merge** (`POST /storefront/baskets/{id}/merge` with `{ "fromBasketId" }`, signed in) moves the lines of a guest basket into the shopper's own basket and deletes the guest one.

How a merge treats things:

- Quantities add up, and a line never goes past 10,000.
- A product that can no longer be bought is left behind.
- A promo code or voucher on the guest basket comes along only when the shopper's basket has none, and a code limited to a group only if they are in it.
- Anything that is not their own basket, or not an open guest basket, is `404`.
- Two merges of the same guest basket at once move its lines once.

## Housekeeping

Two background jobs keep the shop tidy. Each has an interface so you can run it from your own scheduler instead.

### Abandoned baskets

Open baskets nobody has touched for `PiranhaCommerce:Ecommerce:AbandonedBasketDays` (default 60; `0` keeps them for ever) are deleted with their lines. The job runs every six hours, first a minute after start.

- Any change to a basket counts as being active: a line, a promo code, a voucher, or a shopper claiming it.
- Checked-out baskets are never touched.
- Baskets hold no stock, so this is housekeeping only.
- `IBasketHousekeeper` does the work if you want to run it yourself.

### Unpaid orders

Off by default. With `PiranhaCommerce:Ecommerce:UnpaidOrderHours` above 0, an order still at Placed with no payment of any kind after that many hours is cancelled by a job that runs every 15 minutes. It is exactly as if staff had cancelled it: the stock goes back on the shelf, the promo use is given back, and the buyer gets the cancellation email. The movement is recorded as done by "system (unpaid order expired)".

- Part-paid orders, orders already in progress and orders with nothing to pay are never touched.
- A payment that lands a moment before is detected inside the cancel and wins.
- A payment that arrives after is refused like any payment on a cancelled order (a gateway payment lands on the unreconciled list; see [Payments](payments.md)).
- Leave it off if you take orders on account or record payments by hand later.
- `IUnpaidOrderExpiry` does the work if you want to run it yourself.
