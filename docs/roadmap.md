# Roadmap and open design notes

Things we have agreed need doing but have not built. Each entry records the requirement as stated, so the design starts from it.

## Stock transfers between locations

Stock can be held at several locations (`StockLocation`) but cannot yet be moved between them.

Requirements:

- A transfer is **sent by a user at the sending location** and **received by a user at the receiving location**. Two people, two
  steps; neither can complete the other's step.
- Between sending and receiving the stock is **in transit**: it has left the sender's level and is not yet in the receiver's. It
  must be visible as in transit (not lost, not double counted), per product and per route.
- Every step is recorded in the stock ledger, so the ledger still explains every level. Sending writes a negative movement at the
  sender; receiving writes a positive movement at the receiver. The quantity received may differ from the quantity sent (damage,
  miscount); a difference needs to be recorded and flagged, like negative stock.
- A transfer that is never received must stay visible as outstanding.

This depends on **location-aware sign-in** (below): "sent by a user at the sending location" needs to know where a user is.

## Signing in at a location

Today there is one kind of admin user, with no location. Needed: a user can sign in *at* a particular location, so actions such as
sending or receiving a transfer, stock adjustments and (later) EPOS sales are attributed to, and restricted to, that location.

Open questions to settle when this is designed:

- Is a user assigned to one or several locations, and chooses one at sign-in when several?
- Which permissions are per location (send/receive transfers, adjust stock) and which are global (reports, catalog editing)?
- How this relates to the EPOS till: a till belongs to a location, so signing in at a till implies the location.

## Customers: personal data

Customers hold personal data. Before orders exist we need an approach for erasing or anonymising a customer who has orders (the
orders must survive for accounting). Orders will keep a snapshot of the addresses used, so anonymising a customer does not change
history.

## Not yet built (agreed direction)

- Translations for the admin UI and storefront (user-facing text is kept out of the data model so this needs no migrations).
- Customer groups (named in the vocabulary; for segmentation and pricing).
- Customer storefront login (a customer is separate from an admin user).
- Alerts and recount tasks for negative stock (the report and per-movement balances exist).

## Security hardening

- Rate limiting on `POST /api/v1/auth/token` and the admin sign-in POST. Lockout (5 failures) caps guessing per account, but a burst of
  guesses already in flight when the lock lands is still evaluated, and lockout does nothing against one guess each at many accounts.

## Tax rules: rates per product, not just per country

Today a tax rule is one rate per destination country (plus a `*` fallback), charged on goods plus shipping. That is not enough for
most real shops. The UK alone has a 20% standard rate, a 5% reduced rate and a 0% rate, and which one applies depends on the
product, not the destination.

Needed:

- **Tax classes** on products (for example standard, reduced, zero), so several rates can exist for one country.
- Rules keyed on class and country, with an optional region for places that tax by state or province.
- Tax worked out per order line and stored on the line (name, rate, amount), so history never changes. Order tax is the sum of the
  line taxes plus tax on shipping. This changes the order-line snapshot and needs a migration for both providers.
- Which rate shipping is taxed at (often the rate of the goods, or the standard rate). This varies by country and must be a rule.
- Tax-inclusive pricing (prices entered with tax included), since many shops show prices that way.
- Several taxes on one order (for example federal plus provincial) and exemptions (tax-exempt customers or goods).

Open question: whether to model a product's tax class in Catalog (shared with the EPOS) or in Ecommerce. The EPOS will need the
same rates, so Catalog or a small shared tax module is more likely.

## Baskets and orders: decisions and what is left

Decided: baskets and orders sit behind the existing API authorisation (read policy to read, write policy to change). There is no
storefront identity yet, so a storefront backend calls with its own token and keeps the unguessable basket id for the shopper. When
customer storefront login arrives, baskets can be owned by a customer and anonymous baskets merged on sign-in.

Built: basket (lines, live prices, published products only); checkout in one transaction (order, numbered copy of names/prices/addresses,
stock taken from the shelf); order stages Placed, Processing, Completed, Cancelled (cancelling returns stock once); admin order pages.

Not built yet:

- Discounts/promo codes and gift vouchers (they will adjust the total before shipping and tax).
- Payment gateways. Payments are recorded (`manual` provider: cash, bank transfer, terminal) and a gateway plugin can register an
  `IPaymentProvider` and record payments when its gateway confirms; there is no hosted checkout, card capture or webhook handling yet.
- Shipping: flat price per method (optionally free above a spend). No weight/zone rates, carriers or live rates.
- Tax: one rule per destination country, charged on goods plus shipping, prices entered tax-exclusive. No per-product tax classes,
  tax-inclusive pricing, exemptions, or regional (state) rates. See "Tax rules: rates per product" above.
- Fulfilment: parcels and tracking, partial shipment, refunds and returns (restock decisions), partially cancelled orders.
- Clean-up of old open baskets; reserving stock while a shopper is in checkout (today stock is taken only when the order is placed).
- Per-order stock location (every online order currently draws from `Piranha:Ecommerce:StockLocationId`, or the default location).
- One currency per store (`Piranha:Ecommerce:Currency`, default GBP).
- Quantities are whole numbers in baskets; stock itself supports fractions for channels that need them.
- Order emails and notifications; guest checkout versus customer account rules; order history for the shopper.
