---
title: Shipping, returns and refunds
order: 50
description: Shipping methods, rates by weight, zones and carriers, parcels, returns and refunds.
---

## Shipping methods

(`/shipping-methods`): a name and flat price, optionally free once the basket reaches a spend. Once any method is
active, checkout must choose one; with none, shipping is free.

## Shipping by destination and weight

(`/shipping-methods/{id}/rates`, admin: edit a method on Orders > Shipping, and a product's weight in its Online
store settings): a method with no rates charges its flat price everywhere, as before. Give it rate rows (a country, or `*` for every other country,
and a weight band in grams: from the first figure up to, but not including, the second; empty for no limit) and it charges by where the parcel goes and
what the basket weighs (each product's weight times the quantity; a product with none set counts as nothing). Rows for the destination country take
precedence over the `*` rows, and a destination or weight no row covers is not served: the method is refused at quote and checkout, and left out of
`GET /baskets/{id}/shipping-options?countryCode=GB` (also under `/storefront/baskets`), which lists what a basket can use with what each costs now. A
method's "free when the basket reaches" still applies on top of a rate. Rates are replaced as a whole set (a PUT; an empty list goes back to the flat
price) and bands for one country may not overlap. A product's weight is part of its listing, so a PUT of the listing without it clears it.

## Shipping zones and carriers

(`/shipping-zones`, `/shipping-carriers`, admin: Orders > Shipping zones and Shipping carriers): a zone groups countries, or
only some postcode areas of a country (`GB` with `IV, HS, KW` for the Highlands; `GB` with `BT` for Northern Ireland; `*` for everywhere else). A destination is
in one zone only (a country and postcode start can be in just one) and is resolved by the longest matching postcode start, then the zone holding its whole
country, then the `*` zone. Postcodes are compared in upper case without spaces, and a start of letters only matches when a digit follows, so `B` means B1 to B99
and not BA, BB or BT (write `BT`). A rate row gives a `zoneId` instead of a `countryCode`; a method uses countries or zones, never both, and a method priced by
zone serves only the zones it has rows for (it never falls back to a neighbouring zone's price). A zone that rates use cannot be deleted. Pass the delivery
`postalCode` to a quote (`QuoteInput`) and to `shipping-options` (query `postalCode`); checkout uses the shipping address's postcode, and with no postcode the
country as a whole is used. A carrier has a name and an optional tracking link containing `{tracking}`; give `carrierId` when recording a shipment and its name
and a tracking link built from the number are kept on the parcel (a `trackingUrl` you give wins; free-text carrier still works; deleting a carrier leaves sent
parcels as they were). Not built: rates by basket value or item count, live carrier rates, labels, pick-up in store, delivery dates, dimensional weight.

## Sending, returns and refunds

(admin: the order page; API under `/orders/{id}`):
- **Parcels** (`POST /shipments`): record what left, with an optional carrier, tracking number and https tracking link. Only a Processing
  order ships. Leave the lines out to send everything left. When the last item goes the order becomes Completed in the same transaction;
  marking an order completed by hand records anything not yet sent as one shipment, so Completed always means everything has been sent.
  Sending does not touch stock (it came off the shelf when the order was placed). Once anything has been sent the order can no longer be
  cancelled (a cancel would put sent goods back on the shelf): record a return instead. Shoppers see their own order's parcels and
  tracking, without the member of staff. Simultaneous parcels cannot send more than was bought (a conditional update guards each line).
- **Returns** (`POST /returns`): record goods when they arrive back, per line, with a reason. Only what was sent and not already returned
  can come back, on Completed orders too. Each line is put back on the shelf by default; untick for damaged goods (recorded, not
  restocked). The return suggests a refund (price less its share of discount, plus tax; pieces are worked out from the cumulative share so
  three returns of one-of-three refund the line exactly, with the delivery charge optional and allowed once). A return moves no money.
- **Refunds**: a refund is a payment record, as before (`POST /refunds` for money returned elsewhere). When the payment was taken through a
  gateway that can refund (`IRefundingGateway`, which Stripe implements), `POST /orders/{id}/payments/{paymentId}/refund` sends the money
  back through the provider and books it with the provider's refund id, so the provider's own webhook is recognised as the same refund.
  The amount is capped by what that provider took and has not returned, a repeated request with the same `idempotencyKey` is the same
  refund, and a refund the provider is still working on is booked when its webhook says it succeeded. A refund made in the provider's
  dashboard is booked automatically; do not also record it by hand. A gift voucher's portion is not refunded to a card: use the voucher return below.

## Buyer-requested returns

(storefront `POST|GET /orders/{id}/return-requests`, `DELETE .../{requestId}`; staff `/return-requests`,
`/return-requests/{id}/approve|reject`, `GET /orders/{id}/return-requests`; admin: Returns on the order page): a signed-in shopper asks to send
back items of their own order. Only goods that were sent and are not already returned or asked for can be requested (open requests hold their
quantity back; a cancelled order cannot be asked about). The shop's alert address is emailed (`alert.return-requested`); staff approve or turn
down with an optional message and the buyer is emailed (`return.answered`); the buyer can withdraw a request nobody has answered. A request
moves no stock and no money: when the goods arrive, record the return as before, naming the approved request (`returnRequestId`) so it is
marked Received, then refund as usual. Shoppers never see which staff member answered. Erasing a shopper clears both sides' notes and
withdraws unanswered requests.
