---
title: Payments
order: 60
description: Gateways, Stripe, gift vouchers, disputes and unreconciled payments.
---

This guide covers how money gets onto an order: payment gateways and the Stripe plugin, recording payments by hand, gift vouchers, card disputes, and the list where money that could not be booked is kept so it is never lost.

## Recording payments

`POST /orders/{id}/payments` and `POST /orders/{id}/refunds` record money in and out. The record is append-only, so a mistake is corrected with a refund.

- A payment can never take an order past its total, and a refund can never exceed what was received. Simultaneous requests are checked one after another.
- A provider `reference` is unique per provider, so a retried webhook or a double click is refused (`409`) instead of recording the money twice.
- The order shows a `paymentStatus`: `Unpaid`, `PartlyPaid`, `Paid` or `Refunded`.

To stop unpaid orders moving to Processing, set `Piranha:Ecommerce:RequirePaymentBeforeProcessing` to `true`.

## Payment gateways

A gateway is a plugin that takes card (or other) payments through a provider. A gateway package:

- implements `IPaymentGateway`, which is an `IPaymentProvider` (a `Name` such as "stripe" that is stored on every payment, and a `DisplayName`) plus two methods: `StartAsync` opens a payment and returns where to send the shopper, and `ReadWebhookAsync` verifies the gateway's signature and says what happened;
- registers with `services.AddPaymentGateway<T>()`.

The core does the rest.

| Call | Who | What it does |
|---|---|---|
| `POST /orders/{id}/payments/start` | staff | Asks the gateway to collect exactly what is still owed. |
| `POST /storefront/orders/{id}/pay` | the shopper, on their own order | The same. |
| `POST /payments/webhooks/{provider}` | the gateway (public) | Records confirmed payments and refunds. |

How webhooks are treated:

- Replays are harmless, because the provider reference is unique.
- A bad signature is a bare `400`. The gateway must check it in `ReadWebhookAsync` and throw `GatewayWebhookRejectedException`, because this endpoint is public and the signature is the only thing that makes the call trustworthy.
- Bodies over 256 KB are refused.
- Money the gateway confirms that cannot be booked (a cancelled order, over-payment, a wrong currency) is logged as an error and counted as `unreconciled` rather than retried. See [Unreconciled payments](#unreconciled-payments).
- Put `GatewayStartRequest.OrderId` into the gateway's metadata when you start a payment: the webhook must carry it back, because that is how the money is matched to the order.

A gateway that can refund implements `IRefundingGateway` (Stripe does). See [Shipping, returns and refunds](shipping.md) for how refunds through the provider work.

A payment taken outside any gateway (cash, a bank transfer, a card terminal, a cheque) is recorded by staff through the endpoints above. A provider only names itself; recording money is done through `IPaymentService`.

## Stripe

`PiranhaCommerce.Payments.Stripe` (register it with `.AddPlugin<StripePlugin>()`) is the gateway that ships with Piranha Commerce. It uses Stripe Checkout, a page Stripe hosts, so card numbers never touch your server.

**Settings** under `Piranha:Stripe`: `SecretKey`, `WebhookSecret`, `SuccessUrl` and `CancelUrl` (set by you, never by a caller). Keep the two secrets in user secrets or your host's secret store, never in source control.

**Webhook.** Point a Stripe webhook endpoint at `/api/v1/payments/webhooks/stripe` for these events:

- `checkout.session.completed`
- `checkout.session.async_payment_succeeded`
- `refund.created`
- `refund.updated`

To handle disputes and failed refunds as well, also send `charge.dispute.created`, `charge.dispute.closed` and `refund.failed` (see below). Locally, `stripe listen --forward-to localhost:5280/api/v1/payments/webhooks/stripe` prints the `whsec_...` secret to use.

**Safety.** Webhook signatures are checked in constant time with a 5 minute tolerance, and an unset webhook secret rejects everything. Amounts go to Stripe in minor units (pence; whole yen). Three-decimal currencies are refused.

**Return pages.** The sample app serves plain `/thanks` and `/cart` pages (fixed text, nothing from the address echoed) for `SuccessUrl` and `CancelUrl`: `http://localhost:5280/thanks?session={CHECKOUT_SESSION_ID}` and `http://localhost:5280/cart` locally. A real shop points them at its own pages.

Not built: saved cards, Stripe Tax and three-decimal currencies.

## Gift vouchers

Vouchers (`/vouchers`; shoppers use `POST|DELETE /baskets/{id}/voucher`) are a way of **paying**, applied after tax. A voucher never changes the price or the tax: it pays part or all of the total, and the order records a `voucher` payment.

**The code.** It has 80 bits of randomness, is shown once at issue, and only its hash is stored (plus the last four characters). Every failure (unknown, off, expired, empty) gets the same answer.

**The balance.** It moves with one conditional update, and every change is a ledger row (balance = sum of the ledger), so simultaneous orders cannot overspend it.

**Giving money back.**
- Cancelling an order returns what it paid onto the voucher.
- Staff can also return part of it without cancelling: `POST /orders/{id}/refunds/voucher` (admin: Payment > Return to the gift voucher). It is never more than the voucher paid less what already went back, and a later cancel returns only the rest. It is a `voucher` refund on the order and a Returned row in the voucher's ledger, and the buyer is not emailed.
- Staff cannot record a `voucher` payment or refund by hand.

Vouchers are never deleted; switch them off.

## Disputes and failed refunds

A **dispute** is a buyer asking their bank to reverse a card payment. A **failed refund** is a refund the provider could not complete.

Stripe reports these as `charge.dispute.created`, `charge.dispute.closed`, and `refund.failed` (or a failed `refund.updated`). Gateways report them as `GatewayEventKind.DisputeOpened`, `DisputeWon`, `DisputeLost` and `RefundFailed`. Staff see them at `GET /disputes` and `GET /orders/{id}/disputes`, and on the Disputes page in the admin.

### What happens

- **Opened.** The dispute is recorded once (by the provider's id, so replays are harmless), and the shop's alert address is emailed (`alert.dispute-opened`) with the reason and the date to answer by. You respond in the provider's own dashboard, or with the evidence form below.
- **Won.** No money moves.
- **Lost.** It is booked as a refund on the order (the money has gone back to the card), with no email to the buyer. If it cannot be booked (for example the shop already refunded in full) it still closes, and waits on the unreconciled list.
- **A refund that fails at the provider** is listed on the unreconciled list too, and alerts the shop, because the buyer has not been paid back. Deal with it at the provider, then resolve it. Retrying cannot book these.
- **The provider's fee** for the dispute is kept on it (`fee`, in the dispute's currency, updated as the provider reports it, so it shows a fee that is returned when the shop wins). It is a cost to you, not part of the order.

### Sending evidence

`POST /disputes/{id}/evidence` (write access) sends what the shop gives (product description, customer name and email, shipping address, carrier, tracking number, shipping date and notes) through the provider, when it supports that. Stripe does; other gateways opt in with `IDisputeEvidenceGateway`. The Disputes page has an Evidence form.

- `submit: false` saves it at the provider and can be repeated.
- `submit: true` sends it to the bank and is **final** (`evidenceSubmittedUtc`).
- A decided dispute, one already sent, an empty form, or evidence the provider refuses is refused (`409`).

### Documents

A file store plugin is needed. `POST /disputes/{id}/files?kind=` (multipart `file`; write access) attaches a PDF, JPEG or PNG (checked by its contents) to an open dispute whose evidence has not been sent.

- `kind` is `Receipt`, `ShippingDocumentation`, `CustomerCommunication`, `ServiceDocumentation`, `CancellationPolicy`, `RefundPolicy`, `CustomerSignature` or `Other`. There is one document per kind.
- `GET /disputes/{id}/files` lists them, `GET .../files/{fileId}` downloads one, and `DELETE` removes it (not once the evidence is sent).
- Limits are `Piranha:Ecommerce:DisputeFiles:MaxBytes` (5 MB each) and `MaxTotalBytes` (4.5 MB together, which is Stripe's limit).
- The documents go to the provider with the next save or send of the evidence. For Stripe, each is uploaded to Stripe Files and named in the dispute. Documents alone count as evidence. A gateway receives them as `DisputeEvidence.Documents`.
- The Evidence form has upload, list, download and remove.
- The files are not deleted from the store afterwards. They are kept as the record of what was sent.

## Unreconciled payments

Money a gateway confirmed that could not be booked (a cancelled order, over-payment, a wrong currency, an unknown order) is saved in `/unreconciled-payments` (admin: Orders > Unreconciled payments) instead of being lost in a log.

- Each item can be **retried** (it books if the order can now take it) or **resolved** with a note of what you did.
- A replay of the same webhook never lists it twice, and a resolved one is not brought back.
- `GET /unreconciled-payments/open-count` gives the number still open, for a health check.

**Alerts.** Set `Piranha:Ecommerce:AlertEmail` (one or more addresses, comma-separated) and the shop is emailed the first time money is listed (template `alert.unreconciled-payment`, editable like the others). The email has the provider, amount, order reference, reason and the provider's id. A retried webhook for the same money does not email again, and a mail failure never fails the webhook: the item is on the list and in the log either way.
