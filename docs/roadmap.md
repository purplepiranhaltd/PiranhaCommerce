# Roadmap and open design notes

Things we have agreed need doing but have not built. Each entry records the requirement as stated, so the design starts from it.

## Gaps compared with nopCommerce (the order we plan to close them)

Last refreshed 24 November 2026 against nopCommerce 4.90 (released October 2025). The nopCommerce side comes from its public feature pages and release write-ups
(several from third parties), not from running it, so treat the detail as indicative. PiranhaCommerce is headless: an API and an admin, with a sample shop front but no
themes or CMS, so storefront and CMS features are out of scope by design. The two differ in kind: nopCommerce is a finished shop you configure, PiranhaCommerce is the commerce
engine you build a shop on. Order of work, each marked built or open:

1. **Product options and variants: built** (see the README), with pictures per variant. Open: a price as an adjustment to the parent, renaming values.
2. **Product images and media, with SEO fields: built** (see the README): a file store plugin point (local disk, Azure Blob), pictures with sizes made by an imaging package (SkiaSharp), slugs and page text.
   Open: slug history and redirects, bulk upload and video. (Dispute evidence files are built.)
3. **Catalogue import and export: built** (see the README), as CSV with variants, categories, barcodes, stock and the online listing, checked before anything is saved.
   Open: Excel files, stock at other locations, group prices, pictures by address, and large files imported in the background.
4. **Reports and the audit log: built.** Reports (API `/reports/sales|products|categories|customers|tax`, `GET /stock/low`; admin Reports > Orders and Reports > Low stock) cover sales by day, week or month,
   best sellers (optionally variants under their parent), sales by category, top customers, tax by rate and low stock, each as JSON or CSV. Orders that were not cancelled count on the day placed (UTC).
   The audit log (package `PiranhaCommerce.Audit`, admin page Audit log) records who created, changed or deleted catalogue, price, promotion, tax, shipping, customer-group, customer and order records. Open: profit (no cost price held), reports by sales channel, charts, and for the log: entries for other record types, history panels on the product and order pages, and keeping it to a set number of days.
5. **Quantity breaks and minimum or maximum order quantities: built** (see the README). **The discount engine: built** (automatic discounts, free-shipping codes, discounts on chosen products or categories, buy-X-get-Y, per-customer limits, stacking; see the README).
   Open for discounts: a discount on the order total (not goods), tiered spend thresholds, "spend X get a free gift", and gift-with-purchase products. Open for breaks: a variant does not inherit its parent's breaks, breaks cannot be set by catalogue import, quantity steps (multiples of 6) and
   limits per customer.
6. **Outbound webhooks: built** (order placed and stage changed, payment received and refunded, shipment, return, dispute opened and closed; see the README). Open for webhooks: events for product,
   customer and stock changes, a per-endpoint rate limit, and blocking addresses that resolve to private networks.
7. **Shipping zones and carriers:** built (zones by country and postcode area, rates by zone, carriers with tracking links). Rates by basket value, live carrier rates, labels, pick-up in store and delivery dates: open.
   **Later: the real carriers.** Live rates and labels for the carriers the shop actually uses (Royal Mail, DPD, Evri, Parcelforce, ShipStation and so on): needs the owner's carrier accounts and
   a choice of which first. Never call a carrier from inside the checkout transaction; fetch rates before it and keep the chosen price on the quote.
8. **Multi-currency and translations:** open, heaviest design work (see the Translations section).
9. **Reviews, back-in-stock requests, wishlists and related/cross-sell products: built** (see the README). **Brands: built** (with filter counts on the listing). **Specification attributes: built** (including the CSV import and export). **Staff low-stock alerts: built** (one digest to `AlertEmail`, once per dip). **Review replies from the shop: built** (the reviewer is emailed the first time). **Tags: built** (browse by link, searchable, in the CSV). **Review photos: built** (re-made to strip location data, hidden until approved). Nothing open from this item. **"Was this helpful" votes: built.**
10. **Multi-store and vendors:** open, only if a use case needs them.

Also open from the comparison, with where each stands:

- **Payments:** other gateways (PayPal, Adyen and others). Stripe, gift vouchers, refunds, disputes and reconciliation are built.
- **Shipping:** live carrier rates and labels (item 7). Zones, rates and tracking links are built.
- **Currency and language:** multi-currency and translations (item 8).
- **Accounts:** social sign-in and two-factor, reward points, customer-defined attributes, and finer staff permissions than administrator or read-only.
- **Marketing and content:** a newsletter list (nopCommerce 4.90 has several), multiple wishlists per shopper, request-a-quote, and AI help with product text. Blogs, news, polls, forums and themes are out of scope.
- **Operations:** message queueing with retries, a scheduled task view, search-engine integration (Elasticsearch and the like), and a shared (Redis) rate limiter.
- **Selling through others:** multi-store and vendors who sign in to manage their own products and orders (item 10).
- **Ecosystem and accessibility:** nopCommerce has fifteen-plus years in production, a plugin and theme marketplace and a partner network, and 4.90 added accessibility changes for the European Accessibility Act. PiranhaCommerce has none of these; an accessibility pass on the admin and the sample shop is worth doing.

Where PiranhaCommerce is ahead or different: weighed and fractional stock, locations and transfers with a ledger, low-stock alerts, payment reconciliation and disputes, shopper and staff
privacy tools (export and erasure), reviews with photos, shop replies and helpful votes, an API for everything with signed webhooks and an audit log, specifications with filter
counts, and plugins as NuGet packages (host-owned migrations) rather than uploaded DLLs. PiranhaCommerce is closed source; nopCommerce is open source with a paid licence option.

## Stock transfers and staff locations

Built (see the README): a transfer is sent by one person and received by another, in transit between, with every step in the stock ledger; a
difference between sent and received is recorded and flagged; a staff user can be limited to certain locations.

How the open questions were settled, so they can be revisited:

- **Assignment, not sign-in.** A staff user is assigned to zero or more locations (`PUT /staff/{userId}/locations`). Zero means not limited (this keeps
  every existing user working). The location an action happens at is named on the action (send from X, receive at Y, adjust at Z) and checked against
  the assignment, so a person who works at two sites needs no "choose a location" step at sign-in.
- **Per location:** sending, receiving, cancelling and manual stock adjustments. Everything else (catalogue, reports, orders) is global.
- **Two people:** the receiver must be a different user from the sender, unless `PiranhaCommerce:Stock:AllowSameUserToReceive` is set for a one-person shop.

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
- Shipping: a flat price per method (optionally free above a spend), or rates by destination country and basket weight. Zones (countries or postcode
  areas) and carriers with tracking links are built. Not built: rates by basket value or item count, dimensional weight, live rates and labels.
- Tax: classes and per-country rates are built, prices are entered tax-exclusive; see "Tax: what is left" above.
- Fulfilment: parcels, tracking, partial shipment, returns (with a restock choice per line) and refunds through the provider are built. Not built:
  return labels, exchanges, automatic expiry of unanswered return requests,
  and refund status updates for a provider refund that later fails.
- Stock is taken when the order is placed (checkout is one step, so there is no separate hold). An order nobody pays no longer holds it for ever: with
  `UnpaidOrderHours` set, unpaid orders are cancelled and the stock put back (see the README). Not built: holding stock for a basket before checkout.
  Old open baskets are cleared automatically (see the README).
- Per-order stock location (every online order currently draws from `PiranhaCommerce:Ecommerce:StockLocationId`, or the default location).
- One currency per store (`PiranhaCommerce:Ecommerce:Currency`, default GBP).
- Quantities are whole numbers in baskets; stock itself supports fractions for channels that need them.
- Order emails and notifications: built (see the README); guest checkout rules are the `AllowGuestCheckout` setting.
