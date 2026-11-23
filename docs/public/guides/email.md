---
title: Email
order: 70
description: Sending email, templates and the SMTP package.
---

The platform sends email only through `IEmailSender` (`PiranhaCommerce.Core.Email`): one method, `SendAsync(EmailMessage)`. Nothing
in the core knows about SMTP or any provider. Until you register a sender, mail is **not sent** and a warning is logged (without
the content, which may hold a reset link). To send real mail, add a plugin or your own class:

```csharp
builder.Services.AddSingleton<IEmailSender, MySmtpSender>();   // after AddPiranhaCore(): the last registration wins
```

**An email is a key plus data, never words.** `EmailMessage` is `(To, TemplateKey, Variables)`: there is no subject or body in it. The package
that sends an email declares it (`IEmailTemplateContributor`: key, name, and each variable with a description and sample value) and fills in the
variables; the words belong to whichever email sender is installed. The variable names are a public contract, since templates refer to them.

- **SMTP** (`PiranhaCommerce.Email.Smtp`, with `PiranhaCommerce.Email.Smtp.Admin` for the admin section): sends through any SMTP server using
  MailKit, and keeps the wording itself. Each email's subject, text and optional HTML are Liquid templates (via Fluid: loops and conditions, no
  way to run code), edited in **Email > Email templates** or through `/api/v1/email-templates`. Each starts with the shipped wording; "reset"
  goes back to it. A template that does not parse, or cannot render with the email's sample data, is refused on save. HTML bodies HTML-encode
  every variable; text bodies and subjects do not, and a subject is forced onto one line. An email with no stored or shipped wording
  (a third-party package's key you have not written yet) fails to send with a clear message, and the admin lists it as "not set up".
  Settings under `Piranha:Email:Smtp`: `Host`, `Port` (587), `Security` (`StartTls` default, `SslOnConnect`, `Auto`, or `None` for a local mail catcher
  only), `Username`, `Password`, `FromAddress`, `FromName`, `ReplyToAddress`, `TimeoutSeconds`. Keep the password in user secrets or your host's secret
  store. Server certificates are always validated. The recipient must be exactly one valid address, and neither recipient nor subject is logged.
  Run a mail catcher such as smtp4dev or Papercut and set `Security` to `None` for development.
- **A provider with its own templates (Mailjet, planned)**: its package maps each key from `IEmailTemplateCatalog` to a template id and sends the
  variables; the words are edited in the provider's own admin, so with it installed (instead of SMTP) there is no template section here.

Today it sends password-reset links and, for every order placed, a plain-text order confirmation (`Piranha:Ecommerce:SendOrderConfirmations`,
default on). A failing mail server never fails the request that triggered it: the order is still placed, and the failure is logged.
The email is sent before the checkout response returns, so a slow provider slows checkout; queue inside your sender if that matters.
It also emails the buyer when **a parcel is sent** (`order.shipped`: carrier, tracking, what is in it and what is still to come), when **an order
is cancelled** (`order.cancelled`, saying what is still to be refunded if they paid) and when **a refund is recorded** (`order.refunded`: the
amount and the total refunded so far), however the refund was booked (by hand, started from the admin, or the provider's webhook). These
go out after the change is saved and are controlled by `Piranha:Ecommerce:SendOrderUpdates` (default on). Marking an order completed by hand
sends no shipped email (there is no parcel or tracking to tell them), and crediting a gift voucher on cancel is not announced as a refund.
Each is a template in Email > Email templates (SMTP) with its variables listed there; the variable names are the contract for other senders.
