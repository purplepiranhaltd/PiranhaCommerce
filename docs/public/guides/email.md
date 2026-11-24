---
title: Email
order: 70
description: Sending email, templates and the SMTP package.
---

The platform sends email only through `IEmailSender` (`PiranhaCommerce.Core.Email`), which has one method, `SendAsync(EmailMessage)`. Nothing in the core knows about SMTP or any provider.

Until you register a sender, mail is **not sent** and a warning is logged, without the content, which may hold a reset link. To send real mail, add a plugin or your own class:

```csharp
builder.Services.AddSingleton<IEmailSender, MySmtpSender>();   // after AddPiranhaCore(): the last registration wins
```

## An email is a key plus data, never words

`EmailMessage` is `(To, TemplateKey, Variables)`. There is no subject or body in it.

- The package that sends an email **declares** it with an `IEmailTemplateContributor`: a key, a name, and each variable with a description and a sample value. It fills in the variables.
- The **words** belong to whichever email sender is installed.
- The variable names are a public contract, because templates refer to them.

## Choosing a sender

### SMTP

`PiranhaCommerce.Email.Smtp` (with `PiranhaCommerce.Email.Smtp.Admin` for the admin section) sends through any SMTP server using MailKit, and keeps the wording itself.

**Templates.** Each email's subject, text and optional HTML are Liquid templates (through Fluid: loops and conditions, no way to run code). Edit them in **Email > Email templates** or through `/api/v1/email-templates`.

- Each starts with the shipped wording, and "reset" goes back to it.
- A template that does not parse, or cannot render with the email's sample data, is refused on save.
- HTML bodies HTML-encode every variable. Text bodies and subjects do not, and a subject is forced onto one line.
- An email with no stored or shipped wording (a third-party package's key you have not written yet) fails to send with a clear message, and the admin lists it as "not set up".

**Settings** under `PiranhaCommerce:Email:Smtp`:

| Setting | Meaning |
|---|---|
| `Host`, `Port` | The server. The port defaults to 587. |
| `Security` | `StartTls` (default), `SslOnConnect`, `Auto`, or `None` for a local mail catcher only. |
| `Username`, `Password` | Keep the password in user secrets or your host's secret store. |
| `FromAddress`, `FromName`, `ReplyToAddress` | The sender details. |
| `TimeoutSeconds` | How long to wait for the server. |

Server certificates are always validated. The recipient must be exactly one valid address, and neither the recipient nor the subject is logged.

**In development**, run a mail catcher such as smtp4dev or Papercut and set `Security` to `None`.

### A provider with its own templates (Mailjet, planned)

Its package maps each key from `IEmailTemplateCatalog` to a template id and sends the variables. The words are edited in the provider's own admin, so with it installed (instead of SMTP) there is no template section here.

## What gets sent today

| Email | When | Setting |
|---|---|---|
| Password reset and sign-in links | A shopper or staff member asks for one | |
| Order confirmation (plain text) | Every order placed | `PiranhaCommerce:Ecommerce:SendOrderConfirmations`, default on |
| `order.shipped` | A parcel is sent: carrier, tracking, what is in it and what is still to come | `PiranhaCommerce:Ecommerce:SendOrderUpdates`, default on |
| `order.cancelled` | An order is cancelled; says what is still to be refunded if they paid | `SendOrderUpdates` |
| `order.refunded` | A refund is recorded: the amount and the total refunded so far, however it was booked (by hand, from the admin, or by the provider's webhook) | `SendOrderUpdates` |

Order update emails go out after the change is saved. Marking an order completed by hand sends no shipped email, because there is no parcel or tracking to tell them. Crediting a gift voucher on cancel is not announced as a refund.

Staff and shoppers also get email for returns, disputes, low stock, review replies, back-in-stock requests, email verification and unreconciled payments; each is described on the page of the feature that sends it.

Each email is a template in Email > Email templates (SMTP) with its variables listed there. The variable names are the contract for other senders.

## Failures and speed

A failing mail server never fails the request that triggered it: the order is still placed, and the failure is logged.

The email is sent before the checkout response returns, so a slow provider slows checkout. Queue inside your sender if that matters.
