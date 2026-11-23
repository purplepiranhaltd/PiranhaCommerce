---
title: Payments
order: 60
description: Gateways, Stripe, gift vouchers, disputes and unreconciled payments.
---

## Payment gateways

(plugin point): a gateway package implements `IPaymentGateway` (`StartAsync` opens a payment and returns where to
send the shopper; `ReadWebhookAsync` verifies the gateway's signature and says what happened) and registers with
`services.AddPaymentGateway<T>()`. The core does the rest: `POST /orders/{id}/payments/start` (staff) and
`POST /storefront/orders/{id}/pay` (the shopper's own order) ask for exactly what is still owed, and the public
`POST /payments/webhooks/{provider}` records confirmed payments and refunds. Replays are harmless (unique provider reference), a bad
signature is a bare 400, bodies over 256 KB are refused, and money the gateway confirms that cannot be booked (cancelled order,
over-payment, wrong currency) is logged as an error and counted as `unreconciled` rather than retried. No gateway ships in the box.

## Recording payments

**Payments**: `POST /orders/{id}/payments` and `/refunds` record money in and out (append-only; mistakes are corrected with a refund).
A payment can never take an order past its total, a refund can never exceed what was received, and simultaneous requests are checked
one after another. A provider `reference` is unique per provider, so a retried webhook or double click is refused (409) instead of
recording the money twice. The order shows `paymentStatus`: `Unpaid`, `PartlyPaid`, `Paid`, `Refunded`.

## Requiring payment before processing

Set `Piranha:Ecommerce:RequirePaymentBeforeProcessing` to `true` to stop unpaid orders moving to Processing.

## Writing a payment provider

A gateway plugin registers an `IPaymentProvider` (`Name`, `DisplayName`) and calls `IPaymentService.RecordPaymentAsync` when its

## Stripe

(`PiranhaCommerce.Payments.Stripe`, `.AddPlugin<StripePlugin>()`): the first gateway, using Stripe Checkout (a page Stripe hosts, so card
numbers never touch your server). Settings under `Piranha:Stripe`: `SecretKey`, `WebhookSecret`, `SuccessUrl`, `CancelUrl` (set by you, never by
a caller). Keep the two secrets in user secrets or your host's secret store, never in source control. Point a Stripe webhook endpoint at
`/api/v1/payments/webhooks/stripe` for `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `refund.created` and
`refund.updated`. Locally: `stripe listen --forward-to localhost:5280/api/v1/payments/webhooks/stripe` prints the `whsec_...` to use. Webhook
signatures are checked in constant time with a 5 minute tolerance; an unset webhook secret rejects everything. Amounts go to Stripe in
minor units (pence; whole yen); three-decimal currencies are refused. Not built: saved cards, Stripe Tax, 3-decimal currencies.
The sample app serves plain `/thanks` and `/cart` pages (fixed text, nothing from the address echoed) for `SuccessUrl` and `CancelUrl`
(`http://localhost:5280/thanks?session={CHECKOUT_SESSION_ID}` and `http://localhost:5280/cart` locally); a real shop points them at its own pages.

## Gift vouchers

(`/vouchers`, `POST|DELETE /baskets/{id}/voucher`): a way of PAYING, applied after tax. A voucher never changes the
price or the tax; it pays part or all of the total and the order records a `voucher` payment. The code (80 bits of randomness) is
shown once at issue and only its hash is stored, plus the last four characters; every failure (unknown, off, expired, empty) gets the
same answer. The balance moves with one conditional update and every change is a ledger row (balance = sum of the ledger), so
simultaneous orders cannot overspend it. Cancelling an order returns what it paid onto the voucher. Staff can also return part of it without cancelling
(`POST /orders/{id}/refunds/voucher`, admin: Payment > Return to the gift voucher): never more than the voucher paid less what already went back, a later
cancel returns only the rest, it is a `voucher` refund on the order and a Returned row in the voucher's ledger, and the buyer is not emailed. Staff cannot record a `voucher`
payment or refund by hand. Vouchers are never deleted; switch them off.

## Disputes and failed refunds

(Stripe: `charge.dispute.created`, `charge.dispute.closed`, `refund.failed` / a failed `refund.updated`; `GET /disputes`,
`GET /orders/{id}/disputes`): a buyer disputing a card payment with their bank is recorded once (by the provider's id, so replays are harmless) and the
shop's alert address is emailed (`alert.dispute-opened`) with the reason and the date to answer by: you respond in the provider's own dashboard.
Winning moves no money. Losing is booked as a refund on the order (the money has gone back to the card) with no email to the buyer; if it cannot be
booked (for example the shop already refunded in full) it still closes and waits on the unreconciled list. A refund that fails at the provider is
listed there too, and alerts the shop, because the buyer has not been paid back: deal with it at the provider, then resolve it (retrying cannot book
these). Gateways report these through `GatewayEventKind.DisputeOpened | DisputeWon | DisputeLost | RefundFailed`. The provider's fee for the dispute is kept on it (`fee`, in the dispute's currency, updated as the provider reports it, so it shows
a fee that is returned when the shop wins) and is a cost to you, not part of the order. Evidence: `POST /disputes/{id}/evidence` (write access) sends what the
shop gives (product description, customer name and email, shipping address, carrier, tracking number, shipping date, notes) through the provider when it supports it
(Stripe does; other gateways opt in with `IDisputeEvidenceGateway`). `submit: false` saves it at the provider and can be repeated; `submit: true` sends it to the bank
and is final (`evidenceSubmittedUtc`). A decided dispute, one already sent, or an empty form is refused, and so is evidence the provider refuses (409). The
Disputes page has an Evidence form.
**Documents** (a file store plugin is needed): `POST /disputes/{id}/files?kind=` (multipart `file`; write access) attaches a PDF, JPEG or PNG (checked by its contents) to an open dispute whose evidence
has not been sent; `kind` is `Receipt`, `ShippingDocumentation`, `CustomerCommunication`, `ServiceDocumentation`, `CancellationPolicy`, `RefundPolicy`, `CustomerSignature` or `Other`, one document per kind.
`GET /disputes/{id}/files` lists them, `GET .../files/{fileId}` downloads one and `DELETE` removes it (not once the evidence is sent). Limits: `Piranha:Ecommerce:DisputeFiles:MaxBytes` (5 MB each)
and `MaxTotalBytes` (4.5 MB together, Stripe's limit). The documents go to the provider with the next save or send of the evidence (Stripe: each is uploaded to Stripe Files and named in the dispute);
documents alone count as evidence. A gateway receives them as `DisputeEvidence.Documents`. The Disputes page's Evidence form has the upload, list, download and remove. The files are not deleted
from the store (they are kept as the record of what was sent).

## Unreconciled payments

(`/unreconciled-payments`, admin: Orders > Unreconciled payments): money a gateway confirmed that could not be booked
(cancelled order, over-payment, wrong currency, unknown order) is saved here instead of being lost in a log. Each item can be retried
(it books if the order can now take it) or resolved with a note of what you did. A replay of the same webhook never lists it twice, and
a resolved one is not brought back. `GET /unreconciled-payments/open-count` gives the number still open, for a health check.
Set `Piranha:Ecommerce:AlertEmail` (one or more addresses, comma-separated) and the shop is emailed (template `alert.unreconciled-payment`, editable
like the others) the first time money is listed, with the provider, amount, order reference, reason and the provider's id. A retried webhook for the
same money does not email again, and a mail failure never fails the webhook (the item is on the list and in the log either way).
