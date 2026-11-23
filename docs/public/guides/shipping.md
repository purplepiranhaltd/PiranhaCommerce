---
title: Shipping, returns and refunds
order: 50
description: Shipping methods, rates by weight, zones and carriers, parcels, returns and refunds.
---

This guide follows an order out of the door and back: how delivery is priced, how parcels are recorded, and how returns and refunds work. Everything here is in the online store package, with API endpoints and admin pages for each part.

## Shipping methods

A shipping method (`/shipping-methods`) has a name and a flat price, and can be free once the basket reaches a spend. Once any method is active, checkout must choose one. With none, shipping is free.

A method with no rates charges its flat price everywhere. To price by destination and weight, give it rates.

## Rates by destination and weight

Rates live at `/shipping-methods/{id}/rates`. In the admin, edit a method on Orders > Shipping; a product's weight is in its Online store settings.

A **rate row** is a country (or `*` for every other country) and a weight band in grams: from the first figure up to, but not including, the second. Leave the second empty for no limit.

- A basket's weight is each product's weight times its quantity. A product with no weight counts as nothing.
- Rows for the destination country take precedence over the `*` rows.
- A destination or weight that no row covers is **not served**: the method is refused at quote and checkout, and left out of the list of options.
- A method's "free when the basket reaches" still applies on top of a rate.
- Rates are replaced as a whole set (a `PUT`); an empty list goes back to the flat price. Bands for one country may not overlap.
- A product's weight is part of its listing, so a `PUT` of the listing without it clears it.

To see what a basket can use and what each option costs now, call `GET /baskets/{id}/shipping-options?countryCode=GB` (also under `/storefront/baskets`).

## Zones and carriers

### Zones

A zone (`/shipping-zones`; admin: Orders > Shipping zones) groups countries, or only some postcode areas of a country. For example, `GB` with `IV, HS, KW` for the Highlands, `GB` with `BT` for Northern Ireland, and `*` for everywhere else.

- A destination is in one zone only: a country and postcode start can be in just one zone.
- A destination is resolved by the **longest matching postcode start**, then the zone holding its whole country, then the `*` zone.
- Postcodes are compared in upper case without spaces. A start of letters only matches when a digit follows, so `B` means B1 to B99 and not BA, BB or BT. Write `BT` for Northern Ireland.
- A rate row gives a `zoneId` instead of a `countryCode`. A method uses countries **or** zones, never both.
- A method priced by zone serves only the zones it has rows for. It never falls back to a neighbouring zone's price.
- A zone that rates use cannot be deleted.

Pass the delivery `postalCode` to a quote (`QuoteInput`) and to `shipping-options` (the `postalCode` query value). Checkout uses the shipping address's postcode. With no postcode, the country as a whole is used.

### Carriers

A carrier (`/shipping-carriers`; admin: Orders > Shipping carriers) has a name and an optional tracking link containing `{tracking}`. Give `carrierId` when recording a shipment, and the carrier's name and a tracking link built from the tracking number are kept on the parcel.

- A `trackingUrl` you give wins over the carrier's.
- A free-text carrier name still works.
- Deleting a carrier leaves parcels already sent as they were.

Not built: rates by basket value or item count, live carrier rates, labels, pick-up in store, delivery dates and dimensional weight.

## Sending parcels

Parcels are recorded on the order page in the admin, or with `POST /orders/{id}/shipments`: what left, with an optional carrier, tracking number and https tracking link.

- **Only a Processing order ships.** Leave the lines out to send everything left.
- **Completion follows shipping.** When the last item goes, the order becomes Completed in the same transaction. Marking an order Completed by hand records anything not yet sent as one shipment, so Completed always means everything has been sent.
- **Stock is not touched.** It came off the shelf when the order was placed.
- **No cancelling after sending.** Once anything has been sent the order can no longer be cancelled, because a cancel would put sent goods back on the shelf. Record a return instead.
- **Safe under concurrency.** Simultaneous parcels cannot send more than was bought, because a conditional update guards each line.
- **What shoppers see.** Their own order's parcels and tracking, without the member of staff who recorded them.

## Returns

A return (`POST /orders/{id}/returns`) records goods when they arrive back, per line, with a reason.

- Only what was sent and not already returned can come back, on Completed orders too.
- Each line is put back on the shelf by default. Untick that for damaged goods: they are recorded but not restocked.
- The return **suggests a refund**: the price less its share of any discount, plus tax. Pieces are worked out from the cumulative share, so three returns of one-of-three refund the line exactly. The delivery charge is optional and can be refunded once.
- **A return moves no money.** Refunding is a separate step.

## Refunds

A refund is a payment record. There are two ways to make one:

- **Money returned elsewhere:** `POST /orders/{id}/refunds` records a refund you made some other way.
- **Back through the provider:** when the payment was taken through a gateway that can refund (`IRefundingGateway`, which Stripe implements), `POST /orders/{id}/payments/{paymentId}/refund` sends the money back through the provider and books it with the provider's refund id, so the provider's own webhook is recognised as the same refund.

Rules for provider refunds:

- The amount is capped by what that provider took and has not returned.
- A repeated request with the same `idempotencyKey` is the same refund.
- A refund the provider is still working on is booked when its webhook says it succeeded.
- A refund made in the provider's dashboard is booked automatically. **Do not also record it by hand.**
- A gift voucher's portion is not refunded to a card; use the voucher return instead (see [Payments](payments.md)).

## Returns asked for by the shopper

A signed-in shopper can ask to send back items from their own order.

| Who | Calls |
|---|---|
| Shopper | `POST|GET /orders/{id}/return-requests` and `DELETE /orders/{id}/return-requests/{requestId}` (storefront) |
| Staff | `GET /return-requests`, `POST /return-requests/{id}/approve` or `reject`, and `GET /orders/{id}/return-requests`. Admin: Returns on the order page. |

How it works:

- Only goods that were sent and are not already returned or asked for can be requested. Open requests hold their quantity back. A cancelled order cannot be asked about.
- The shop's alert address is emailed (`alert.return-requested`). Staff approve or turn down with an optional message, and the buyer is emailed (`return.answered`).
- The buyer can withdraw a request nobody has answered.
- **A request moves no stock and no money.** When the goods arrive, record the return as above, naming the approved request (`returnRequestId`) so it is marked Received, then refund as usual.
- Shoppers never see which staff member answered.
- Erasing a shopper clears both sides' notes and withdraws unanswered requests.
