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
- Customer groups: built (named sets of customers, promotions limited to a group). Not built: group pricing, price lists, automatic membership rules, group-limited shipping or payment methods.
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

- **Email**: the plugin point (`IEmailSender`), password reset / first sign-in by emailed link, and order confirmations and order updates (sent, cancelled, refunded) are built.
  SMTP sender is built, with editable templates in its own admin section. Planned: a Mailjet package using its Transactional Email templates (words live in Mailjet; a message is a key plus variables). Not built: other providers (SendGrid, Azure Communication Services - a plugin each), HTML and editable templates, a queue with
  retries (mail is sent inline and a failure is only logged) and a welcome email. A verified email before sign-in is built as an option (`RequireVerifiedEmail`, off by default).
- Staff admin for shopper accounts and ending sessions early are built (a session version on each sign-in, checked on every call; see the README).
  Staff actions on a sign-in are recorded (who, what, when). Refresh tokens are built (single use, rotated, revoked on reuse). Shoppers can list their devices, end one and sign out everywhere.
- Merging two baskets (claim only takes an unowned basket whole); guest checkout through the storefront API.
- Customer data export and erasure are built for staff (orders anonymised, never deleted). Shoppers can export and erase their own account through the storefront API.
  Not built: a scheduled retention clean-up, and erasure in the payment provider and email provider.
- Social sign-in, two-factor.
- Shopper sign-in is rate limited by client address (with the staff limits) but there is no CAPTCHA or breached-password check.

## Baskets and orders: decisions and what is left

Decided: baskets and orders sit behind the existing API authorisation (read policy to read, write policy to change). There is no
shopper identity in this API, so a storefront backend calls with its own token and keeps the unguessable basket id for the shopper.
Signed-in shoppers also have their own `/storefront` endpoints (below), where baskets are owned by the customer.

Built: basket (lines, live prices, published products only); checkout in one transaction (order, numbered copy of names/prices/addresses,
stock taken from the shelf); order stages Placed, Processing, Completed, Cancelled (cancelling returns stock once); admin order pages.

Not built yet:

- Promotions: promo codes are built (percent or fixed amount off the goods, one code per basket). Not built: free-shipping codes,
  discounts on selected products or categories, buy-one-get-one, automatic discounts with no code, per-customer use limits, stacking
  several codes, and refunds that adjust a discount after a partial return.
- Gift vouchers are built (issue, pay at checkout, return on cancel or in part by staff, ledger). Not built: selling a voucher as a product (an order line
  that issues one on payment), voucher use from the shopper side ("check my balance"), expiry
  reminders, and a per-code attempt limit beyond the shared request limiter.
- Payment gateways: the plugin point is built (start a payment, signed webhooks, idempotent recording; tested with a fake gateway).
  Stripe is built (`PiranhaCommerce.Payments.Stripe`). Unreconciled payments are listed in the admin (retry or resolve with a note).
  An email alert goes to `AlertEmail` when something lands on that list. Disputes and failed refunds are recorded and alerted (see the README). Not built: evidence submission, a shopper-facing "payment pending" state, and payment on delayed methods that confirm days later.
- Shipping: a flat price per method (optionally free above a spend), or rates by destination country and basket weight. Not built: zones that group
  countries (list each country), rates by basket value or item count, dimensional weight, carriers, live rates and labels, and free-shipping promo codes.
- Tax: classes and per-country rates are built, prices are entered tax-exclusive; see "Tax: what is left" above.
- Fulfilment: parcels, tracking, partial shipment, returns (with a restock choice per line) and refunds through the provider are built. Not built:
  return labels, exchanges, automatic expiry of unanswered return requests,
  and refund status updates for a provider refund that later fails.
- Stock is taken when the order is placed (checkout is one step, so there is no separate hold). An order nobody pays no longer holds it for ever: with
  `UnpaidOrderHours` set, unpaid orders are cancelled and the stock put back (see the README). Not built: holding stock for a basket before checkout.
  Old open baskets are cleared automatically (see the README).
- Per-order stock location (every online order currently draws from `Piranha:Ecommerce:StockLocationId`, or the default location).
- One currency per store (`Piranha:Ecommerce:Currency`, default GBP).
- Quantities are whole numbers in baskets; stock itself supports fractions for channels that need them.
- Order emails and notifications; guest checkout rules for the storefront API.
