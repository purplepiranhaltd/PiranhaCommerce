---
title: Configuration
order: 50
description: Where each setting lives and which settings every shop should decide on.
---

# Configuration

Settings are ordinary ASP.NET configuration, so they come from `appsettings.json`, environment variables (use `__` for `:`, so `PiranhaCommerce__Jwt__SigningKey`), user secrets or your host's secret store. **Keep secrets (the signing key, database passwords, Stripe keys, SMTP passwords) out of source control.**

Each package reads one section under `PiranhaCommerce:`.

| Section | Package | What it controls |
|---|---|---|
| `ConnectionStrings:PiranhaCommerce` | Data | The database connection. |
| `PiranhaCommerce:Provider` | Host | `SqlServer` (also Azure SQL) or `PostgreSql`. Chooses which migration set the host uses. |
| `PiranhaCommerce:SeedAdmin` | Host (sample) | Email and password of a development administrator, created on startup in Development only. |
| `PiranhaCommerce:Jwt` | Api | `SigningKey` (32 or more characters; the API fails closed without it), `LifetimeMinutes` (60). Shopper tokens use a key derived from it. |
| `PiranhaCommerce:RateLimit` | Api | Sign-in limits: `SignInAttempts`, `WindowSeconds`, `GuestBasketAttempts`, or `Enabled: false`. See [Hosting requirements](../advanced/hosting.md). |
| `PiranhaCommerce:Customers` | Customers | Lockout, `RefreshTokenDays`, `RequireVerifiedEmail`, `ResetUrlTemplate`, `VerifyUrlTemplate` and more. See [Customers and shopper accounts](../guides/shoppers.md). |
| `PiranhaCommerce:Ecommerce` | Ecommerce | `Currency` (GBP), `StockLocationId`, `DefaultCountryCode`, `AllowGuestCheckout`, `RequirePaymentBeforeProcessing`, `SendOrderConfirmations`, `SendOrderUpdates`, `SendReviewReplies`, `AlertEmail`, `AbandonedBasketDays`, `UnpaidOrderHours`, `ReviewsNeedApproval`, `StockAlertPollSeconds`, `StockAlertUnsubscribeUrlTemplate`, `LowStockAlertLevel`, `LowStockAlertPollSeconds`. |
| `PiranhaCommerce:Ecommerce:DisputeFiles` | Ecommerce | Size limits for dispute evidence documents. |
| `PiranhaCommerce:Catalog:Import` | Catalog | `MaxRows` and `MaxBytes` for catalogue import. |
| `PiranhaCommerce:Images` | Catalog | `MaxBytes`, `MaxPerProduct`, and the `Sizes` made at upload. |
| `PiranhaCommerce:Stock` | Catalog | `AllowSameUserToReceive` for stock transfers. |
| `PiranhaCommerce:Storage:LocalDisk` | Storage.LocalDisk | `Path` of the folder for uploaded files (default `App_Data/files`). |
| `PiranhaCommerce:Storage:AzureBlob` | Storage.AzureBlob | `ConnectionString` or `AccountUri`, and `Container`. |
| `PiranhaCommerce:Email:Smtp` | Email.Smtp | `Host`, `Port`, `Security`, `Username`, `Password`, `FromAddress`, `FromName`, `ReplyToAddress`. See [Email](../guides/email.md). |
| `PiranhaCommerce:Stripe` | Payments.Stripe | `SecretKey`, `WebhookSecret`, `SuccessUrl`, `CancelUrl`. See [Payments](../guides/payments.md). |
| `PiranhaCommerce:Webhooks` | Webhooks | `PollSeconds`, `MaxAttempts`, `RetentionDays`. See [Reports, webhooks and audit](../guides/operations.md). |

## Settings every shop should decide on

- **Currency** (`PiranhaCommerce:Ecommerce:Currency`): one currency per store for now.
- **Home country** (`PiranhaCommerce:Ecommerce:DefaultCountryCode`): lets price lookups show tax-inclusive prices.
- **Where stock comes from** (`StockLocationId`): the built-in location when empty.
- **Guest checkout** (`AllowGuestCheckout`): on by default.
- **Order emails and alerts** (`SendOrderConfirmations`, `SendOrderUpdates`, `SendReviewReplies`, `AlertEmail`, `LowStockAlertLevel`): need an email package.
- **Housekeeping** (`AbandonedBasketDays`, `UnpaidOrderHours`): decide how long open baskets live and whether unpaid orders expire.
- **Reviews** (`ReviewsNeedApproval`): on by default. `MaxReviewPhotos` (4, 0 for none) sets how many photos a review can carry.

The guides name every setting in the place it matters.
