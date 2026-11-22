# Roadmap and open design notes

Things we have agreed need doing but have not built. Each entry records the requirement as stated, so the design starts from it.

## Stock transfers and staff locations

Built (see the README): a transfer is sent by one person and received by another, in transit between, with every step in the stock ledger; a
difference between sent and received is recorded and flagged; a staff user can be limited to certain locations.

How the open questions were settled, so they can be revisited:

- **Assignment, not sign-in.** A staff user is assigned to zero or more locations (`PUT /staff/{userId}/locations`). Zero means not limited (this keeps
  every existing user working). The location an action happens at is named on the action (send from X, receive at Y, adjust at Z) and checked against
  the assignment, so a person who works at two sites needs no "choose a location" step at sign-in.
- **Per location:** sending, receiving, cancelling and manual stock adjustments. Everything else (catalogue, reports, orders) is global.
- **Two people:** the receiver must be a different user from the sender, unless `Piranha:Stock:AllowSameUserToReceive` is set for a one-person shop.

Not built:

- Signing in *at* a location (a location claim in the token) and a till that implies one: this is for EPOS, which can add it on top of the assignment.
- Permissions finer than "administrator or read-only" (for example, can send but not adjust).
- Receiving a transfer in parts over several visits, and a write-off record for the difference (today the difference is on the transfer, not on a shelf).
- Transfers raised by the receiving site as a request, and approval before stock leaves.

## Pricing: what is left

Built: sales, and group prices, by group percentage, by category percentage and by fixed price per product (see the README). Agreed to do later:

- **Sale and offer prices:** built (see the README): dated sales by percentage or fixed price on all products, categories or named products, for everyone. Not built:
  a sale for one customer group only, buy-one-get-one and other offers, and recurring sales (copy a sale to run it again).
- **Advertised member prices** (like a supermarket's loyalty-card price): show everyone the member price, but only members get it. Today a shopper
  only sees a different price once they are identified, because the price lookup is by customer. This needs the member price returned alongside the
  regular price for people who are not members, and wording to say who qualifies.
- **Tax-inclusive display:** built for showing (see the README): price lookups and quotes return tax-inclusive figures for a destination. Still open: entering prices
  tax-inclusive (see Tax below), and a way for a shop front to show tax-inclusive figures on a basket before a destination is chosen without calling the quote.
- **Minimum quantities and quantity breaks** ("10 or more at X"): left out for now.

## Translations (on hold, needs design)

Deliberately not started: it needs a lot of thought. Questions to settle first:

- What is translated, and in what order: catalogue text (product, category and shipping method names and descriptions), emails, admin screens, API messages
  (these would first need stable error codes so clients can translate by code).
- Languages: a fixed list in configuration or added by staff; fallback to the default language or hide; one language per language or regional variants.
- Storage: a translations table keyed by item, field and language (no migration per language) or columns per language.
- Who sees which: language from `Accept-Language` or a parameter; a shopper's saved language for emails; the language an order was placed in.
- Entering translations: sections on each admin page, bulk import and export, a report of untranslated items, and whether any machine suggestions.
- Related: translated URL slugs, selling in other currencies and countries, tax-inclusive display.

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
- Guest order lookup (an order placed as a guest can only be seen from the checkout response and the confirmation email), and offering a guest to turn their order into an account.
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
  An email alert goes to `AlertEmail` when something lands on that list. Disputes and failed refunds are recorded and alerted (see the README). Not built: uploading evidence files, a shopper-facing "payment pending" state, and payment on delayed methods that confirm days later.
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
- Order emails and notifications: built (see the README); guest checkout rules are the `AllowGuestCheckout` setting.
