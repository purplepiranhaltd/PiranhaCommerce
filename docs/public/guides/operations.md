---
title: Reports, webhooks and audit
order: 90
description: Reports, webhooks to other systems, the audit log and admin menu badges.
---

This guide covers running the shop: sales and stock reports, telling other systems when things happen, the record of who changed what, and the counts in the admin menu.

## Reports

Reports are at `GET /api/v1/reports/sales`, `/products`, `/categories`, `/customers` and `/tax`, plus `GET /stock/low?threshold=`. In the admin: Orders > Reports and Catalog > Low stock.

**Options every report takes**
- `from` and `to` (dates, inclusive; the last 30 days by default).
- `format=csv` for a spreadsheet file.

**What is counted.** Orders that were not cancelled, on the day they were placed (UTC).

| Report | What it shows |
|---|---|
| Sales | By `interval` (day, week from Monday, or month): orders, units, goods, discounts, shipping, tax, total, refunded (by the day refunded) and net. |
| Products | Ranked by revenue or quantity (`top`, `rollUpVariants`), with units returned. |
| Categories | A product in several categories counts in each. Variants use their parent's. |
| Customers | Top spenders, new customers and guest orders. |
| Tax | By rate, for goods and delivery, before refunds. |

**Limits:** 1100 days per report, and a daily report at most a year.

Not built: profit (no cost price is held), reports by sales channel, and charts.

## Webhooks

Webhooks tell other systems when something happens. Add `PiranhaCommerce.Webhooks`, and `PiranhaCommerce.Webhooks.Admin` for the Webhooks page. The API is `/api/v1/webhooks`, for administrators only, even to read.

### Endpoints and events

An endpoint is an address (`https://`, or `http://` for localhost), a note, and the event types it wants (none, or `*`, for all). `GET /webhooks/event-types` lists them:

`order.placed`, `order.stage_changed`, `payment.received`, `payment.refunded`, `shipment.created`, `return.created`, `dispute.opened`, `dispute.closed`.

Services write each event in the same transaction as the change, so an event exists **if and only if** its change was committed. A checkout that fails on stock leaves none.

### The message

A background sender (every `PiranhaCommerce:Webhooks:PollSeconds`, default 10; `0` turns it off) delivers each event to every endpoint that existed when it happened and wanted it. A message is:

```json
POST { "id": "...", "type": "order.placed", "created": "...", "apiVersion": "...", "data": { } }
```

Messages are **thin**: ids, the order reference, states and money, never names, emails or addresses. Fetch the details from the API.

Headers:

| Header | Meaning |
|---|---|
| `PiranhaCommerce-Signature` | `t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body" with the endpoint's secret>`. Reject old `t` values to stop replays. |
| `PiranhaCommerce-Event-Id`, `PiranhaCommerce-Event-Type` | What happened. |
| `PiranhaCommerce-Delivery-Id`, `PiranhaCommerce-Delivery-Attempt` | Which delivery, and which try. |

### Delivery

Answer 2xx to accept. Anything else, no answer in 10 seconds, or a redirect (never followed) counts as failed and is tried again after 1, 5 and 15 minutes, then 1, 3, 6 and 12 hours (`PiranhaCommerce:Webhooks:MaxAttempts`, default 8). It is then marked Failed.

Delivery is **at least once and in no guaranteed order**: ignore an event id you have already handled.

Several hosts can run the sender at once, because each delivery is claimed with a conditional update.

### Secrets and testing

- The secret (`whsec_...`) is made by the server and shown only when the endpoint is created or `POST /webhooks/{id}/rotate-secret` is called. The old one stops working at once. It is kept out of the audit log.
- `POST /webhooks/{id}/test` sends a `webhook.test` event now.
- `GET /webhooks/{id}/deliveries` is the log: status, tries, the answer and any error.
- `POST /webhook-deliveries/{id}/redeliver` sends one again.
- Events and finished deliveries are deleted after `PiranhaCommerce:Webhooks:RetentionDays` (30).

### Publishing your own events

A package that publishes events registers an `IEventTypeContributor` and takes an optional `IEventPublisher` (in Core). It writes with `Publish` before the `SaveChanges` of its own transaction.

Not built: events for products, customers and stock, and blocking private-network addresses.

## Audit log

Add `PiranhaCommerce.Audit`, and `PiranhaCommerce.Audit.Admin` for the Audit log page. `GET /api/v1/audit?entityType=&entityId=&actor=&from=&to=` reads it, for administrators only.

**What it records.** Every create, change and delete of the records marked `[Audited]`: products, categories, barcodes, pictures, variants and options, stock locations and transfers, customers, addresses and groups, orders, promotions, sales, group prices, tax classes and rules, shipping methods and rates, and gift vouchers. For each: when, who (the signed-in user's email, or `shopper:{id}` for a shopper) and, for a change, each property's old and new value.

**Same transaction.** It is written with the change, so a rolled-back change (including the check pass of an import) leaves nothing.

**Personal data.** Properties marked `[AuditMask]` are recorded as "changed" without the value. That covers customer names, emails, phone, addresses and notes, an order's email and notes, and a voucher's note. Erasing a customer therefore leaves no personal data in the log.

**For package authors.** Mark your own entity classes with `[Audited]` to join in. Services that change rows with set-based SQL either go through the change tracker (`db.DeleteTrackedAsync`, `db.UpdateTrackedAsync`) or say what they changed through `IAuditTrail` (order stage changes do).

**Not recorded:** payments and stock movements (they are ledgers already), baskets, sign-ins, and counters. There is no automatic clearing out of old entries yet.

## Admin menu badges

A menu entry can show a count of things waiting for a person. A plugin sets `AdminMenuItem.BadgeKey` and registers an `IAdminBadgeProvider`. Zero shows nothing, and a failing provider is logged and skipped.

Orders > Unreconciled payments, Disputes (open) and Return requests (waiting) use it. The numbers refresh when you move between pages. Disputes and Return requests also have list pages.
