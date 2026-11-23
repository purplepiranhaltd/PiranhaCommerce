---
title: Reports, webhooks and audit
order: 90
description: Reports, webhooks to other systems, the audit log and admin menu badges.
---

## Reports

(`GET /api/v1/reports/sales`, `/products`, `/categories`, `/customers`, `/tax`; `GET /stock/low?threshold=`; admin: Orders > Reports and Catalog > Low stock). Every report takes `from` and `to`
(dates, inclusive, last 30 days by default) and `format=csv` for a spreadsheet file. They count orders that were not cancelled on the day they were placed (UTC). **Sales** by `interval` day, week (from Monday)
or month: orders, units, goods, discounts, shipping, tax, total, refunded (by the day refunded) and net. **Products** ranked by revenue or quantity (`top`, `rollUpVariants`), with units returned;
**categories** (a product in several categories counts in each; variants use their parent's); **customers** (top spenders, new customers, guest orders); **tax** by rate for goods and delivery, before refunds.
Limits: 1100 days per report, a daily report at most a year. Not built: profit (no cost price is held), reports by sales channel, charts and an audit log of staff changes.

## Webhooks

(add `PiranhaCommerce.Webhooks`, and `PiranhaCommerce.Webhooks.Admin` for the page Webhooks; `/api/v1/webhooks`, administrators only, even to read). Tells other systems when something happens. An endpoint
is an address (`https://`, or `http://` for localhost), a note and the event types it wants (none or `*` for all; `GET /webhooks/event-types` lists them): `order.placed`, `order.stage_changed`, `payment.received`,
`payment.refunded`, `shipment.created`, `return.created`, `dispute.opened`, `dispute.closed`. Services write each event in the same transaction as the change, so an event exists if and only if its change was committed
(a checkout that fails on stock leaves none), and a background sender (every `Piranha:Webhooks:PollSeconds`, default 10; 0 turns it off) delivers it to every endpoint that existed when the event happened and
wanted it. A message is `POST { id, type, created, apiVersion, data }`: **thin**: ids, the order reference, states and money, never names, emails or addresses; fetch details from the API. Headers:
`Piranha-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body" with the endpoint's secret>` (reject old `t` values to stop replays), `Piranha-Event-Id`, `Piranha-Event-Type`, `Piranha-Delivery-Id`,
`Piranha-Delivery-Attempt`. Answer 2xx to accept; anything else, no answer in 10 seconds, or a redirect (never followed) counts as failed and is tried again after 1, 5 and 15 minutes, then 1, 3, 6 and 12 hours
(`Piranha:Webhooks:MaxAttempts`, default 8), then marked Failed. Delivery is **at least once and in no guaranteed order**: ignore an event id you have already handled. The secret (`whsec_...`) is made by the server,
shown only when the endpoint is created or `POST /webhooks/{id}/rotate-secret` is called (the old one stops working at once), and kept out of the audit log. `POST /webhooks/{id}/test` sends a `webhook.test` event now;
`GET /webhooks/{id}/deliveries` is the log (status, tries, answer, error); `POST /webhook-deliveries/{id}/redeliver` sends one again. Several hosts can run the sender at once (each delivery is claimed with a
conditional update). Events and finished deliveries are deleted after `Piranha:Webhooks:RetentionDays` (30). A package that publishes events registers an `IEventTypeContributor` and takes an optional
`IEventPublisher` (Core), writing with `Publish` before the SaveChanges of its own transaction. Not built: events for products, customers and stock, and blocking private-network addresses.

## Audit log

(add `PiranhaCommerce.Audit`, and `PiranhaCommerce.Audit.Admin` for the page Audit log; `GET /api/v1/audit?entityType=&entityId=&actor=&from=&to=`, administrators only). Records every create, change and delete of the records marked
`[Audited]` (products, categories, barcodes, pictures, variants and options, stock locations and transfers, customers, addresses and groups, orders, promotions, sales, group prices, tax classes and rules, shipping methods and rates, gift vouchers),
with when, who (the signed-in user's email; `shopper:{id}` for a shopper) and, for a change, each property's old and new value. It is written in the same transaction as the change, so a rolled-back change (including the check pass of an import) leaves nothing.
Properties marked `[AuditMask]` (customer names, emails, phone, addresses and notes; an order's email and notes; a voucher's note) are recorded as "changed" without the value, so erasing a customer leaves no personal data in the log.
A package marks its own entity classes with `[Audited]` to join in. Services that change rows with set-based SQL either go through the change tracker (`db.DeleteTrackedAsync`, `db.UpdateTrackedAsync`) or say what they changed through `IAuditTrail`
(order stage changes do). Not recorded: payments and stock movements (they are ledgers already), baskets, sign-ins, and counters. There is no automatic clearing out of old entries yet.

## Admin menu badges

a menu entry can show a count of things waiting for a person (`AdminMenuItem.BadgeKey` plus an `IAdminBadgeProvider`
registered by the plugin; zero shows nothing and a failing provider is logged and skipped). Orders > Unreconciled payments, Disputes (open) and
Return requests (waiting) use it; the numbers refresh when you move between pages. Disputes and Return requests also have list pages.
