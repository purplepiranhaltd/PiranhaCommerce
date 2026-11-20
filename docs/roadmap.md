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
- Shopper accounts are built; what is left (email, password reset, token revocation) is under "Shopper accounts: what is left".
- Alerts and recount tasks for negative stock (the report and per-movement balances exist).

## Security hardening

- Built: rate limiting on `POST /api/v1/auth/token` and the admin sign-in POST (10 a minute per client address, IPv6 by /64).
  Still open: limits are per host process, so several instances each allow their own count (a shared store such as Redis would fix
  that); a slow attack spread over many addresses is not stopped by either lockout or this; and the limit is only as accurate as
  the host's forwarded-headers setup.

## Tax: what is left

Built: tax classes (a default "Standard", plus any you add), a rate per class per destination country, tax per order line copied onto
the line, shipping taxed at the default class's rate, a per-rate summary on quotes and orders.

Not built:

- A region (state or province) within a country, for places that tax by region.
- Choosing the rate postage is taxed at: it is always the default class's. Some countries tax postage at the goods' rate, or at the
  highest rate in the basket.
- Tax-inclusive pricing (prices entered with tax included), since many shops show prices that way.
- Several taxes stacked on one line (for example federal plus provincial) and exemptions (tax-exempt customers or goods).
- Rate changes on a date (for example a VAT change at midnight): today a rule's rate is simply edited.
- A product's tax class lives on its **online listing** (Ecommerce), so a till would not see it. When the EPOS arrives, the class
  and rules probably move to Catalog or a small shared tax module.
- The default-class switch is one transaction, but two simultaneous switches could leave two defaults; the lowest id wins until fixed.

## Shopper accounts: what is left

Built: register, sign in (own table, own token audience and key), profile, address book, password change, baskets and checkout owned
by the shopper, order history, claim an anonymous basket.

Not built:

- **Email**: the plugin point (`IEmailSender`), password reset / first sign-in by emailed link, and order confirmations are built.
  Not built: a real sender (SMTP, SendGrid, Azure Communication Services - a plugin each), HTML and editable templates, a queue with
  retries (mail is sent inline and a failure is only logged), welcome and order-status emails (shipped, cancelled, refunded),
  staff-triggered "send invite" from the admin, and requiring a verified email before sign-in (an unverified address can register a
  new account today; the real owner recovers it with forgot-password).
- Staff admin for shopper accounts: nothing in the admin shows which customers have a sign-in, or lets staff disable one.
- Ending tokens early: changing a password or signing out does not revoke tokens already issued (they expire in an hour). Refresh
  tokens and a revocation stamp would fix this.
- Merging two baskets (claim only takes an unowned basket whole); guest checkout through the storefront API.
- Customer groups, customer data export/erasure (above), social sign-in, two-factor.
- Shopper sign-in is rate limited by client address (with the staff limits) but there is no CAPTCHA or breached-password check.

## Baskets and orders: decisions and what is left

Decided: baskets and orders sit behind the existing API authorisation (read policy to read, write policy to change). There is no
shopper identity in this API, so a storefront backend calls with its own token and keeps the unguessable basket id for the shopper.
Signed-in shoppers also have their own `/storefront` endpoints (below), where baskets are owned by the customer.

Built: basket (lines, live prices, published products only); checkout in one transaction (order, numbered copy of names/prices/addresses,
stock taken from the shelf); order stages Placed, Processing, Completed, Cancelled (cancelling returns stock once); admin order pages.

Not built yet:

- Discounts/promo codes and gift vouchers (they will adjust the total before shipping and tax).
- Payment gateways. Payments are recorded (`manual` provider: cash, bank transfer, terminal) and a gateway plugin can register an
  `IPaymentProvider` and record payments when its gateway confirms; there is no hosted checkout, card capture or webhook handling yet.
- Shipping: flat price per method (optionally free above a spend). No weight/zone rates, carriers or live rates.
- Tax: classes and per-country rates are built, prices are entered tax-exclusive; see "Tax: what is left" above.
- Fulfilment: parcels and tracking, partial shipment, refunds and returns (restock decisions), partially cancelled orders.
- Clean-up of old open baskets; reserving stock while a shopper is in checkout (today stock is taken only when the order is placed).
- Per-order stock location (every online order currently draws from `Piranha:Ecommerce:StockLocationId`, or the default location).
- One currency per store (`Piranha:Ecommerce:Currency`, default GBP).
- Quantities are whole numbers in baskets; stock itself supports fractions for channels that need them.
- Order emails and notifications; guest checkout rules for the storefront API.
