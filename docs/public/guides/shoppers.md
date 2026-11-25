---
title: Customers and shopper accounts
order: 20
description: Customers, sign-in and tokens, groups, verified email, privacy, export and erasure.
---

This guide covers the people who buy: the customer record, how shoppers sign in, the tools staff have for managing sign-ins, customer groups, and privacy (export and erasure).

## Customers

`PiranhaCommerce.Customers` holds the people and businesses that buy, for every sales channel.

A customer has:

- an email address (required, and unique whatever the letter case, enforced by a database index),
- an optional name and phone number,
- staff-only notes, and
- an address book of up to 50 addresses (country as an ISO two-letter code).

Orders copy the address they use, so editing an address never changes history.

The API is `/api/v1/customers` and `/api/v1/customers/{id}/addresses`. Reads need the read policy and changes need the write policy.

A customer is deliberately **not** an admin user. See "Kept apart from the staff API" below.

## Shopper accounts (the storefront API)

Shoppers sign in separately from staff. These endpoints are under `/api/v1/storefront`.

### Signing in and registering

`POST /auth/register` and `POST /auth/token` are anonymous. Registration returns a token. A wrong password, an unknown email, a locked account and a deactivated account all answer the same `401`. Five wrong passwords lock the account for 15 minutes (`PiranhaCommerce:Customers`).

Tokens last `PiranhaCommerce:Jwt:LifetimeMinutes` (60). A deactivated customer is cut off at once, even with an unexpired token.

### Refresh tokens

Signing in, registering and changing a password also return a `refreshToken`.

- `POST /auth/refresh` with `{ "refreshToken" }` swaps it for a new access token and the next refresh token. The response has the same shape as signing in. Each refresh token is single use, and only a hash is stored.
- Using a token a second time means it was copied, so **that device's whole chain is revoked**. Two refreshes at the same moment look like this, so an app should refresh from one place.
- `POST /auth/logout` with `{ "refreshToken" }` ends one device's chain (always `204`).
- A refresh token stops working when sessions are ended (sign out everywhere, a password change or reset, switching the customer off, a staff email change), after `PiranhaCommerce:Customers:RefreshTokenDays` (default 30, counted from each use; 0 switches refresh tokens off), and after `RefreshTokenMaxDays` (default 180) since that sign-in began. Anything not live gets the same `401`.
- Refresh tokens are for the shopper API only, never staff.

### Their devices

- `GET /me/sessions` lists the devices that can still refresh: id, a readable `device` such as "Chrome on Windows" taken from the User-Agent when they signed in, `current` for the device making the call, started, last used and expires.
- The User-Agent is cleaned and cut to 200 characters, and no IP address is kept. A shop's own app can send a User-Agent like `MyShop/2.1` and it is shown as it is.
- `DELETE /me/sessions/{id}` ends one (`404` if it is not theirs).
- `POST /me/sign-out-everywhere` ends every session, access tokens included, and returns a new token pair for the device that asked. It is recorded in the sign-in history as done by the shopper.

### Their own record

`GET|PUT /me`, `POST /me/password` and `GET|POST|PUT|DELETE /me/addresses` work on the signed-in shopper's own record only. Staff notes are never shown.

The shopper also uses `POST /baskets`, `GET /baskets/{id}`, `PUT|DELETE /baskets/{id}/lines/{productId}`, `POST /baskets/{id}/quote`, `POST /baskets/{id}/checkout`, `POST /baskets/{id}/claim`, `GET /orders` and `GET /orders/{id}`; see [Baskets and orders](baskets-orders.md).

### Kept apart from the staff API

- A shopper's sign-in lives in its own table, not among the admin users, so a customer can never open the admin or hold a role.
- Shopper tokens have their own audience, signing key (derived from `PiranhaCommerce:Jwt:SigningKey`) and scheme. The staff API refuses them, and the storefront refuses staff tokens. Every storefront endpoint requires a shopper token unless it says otherwise.
- The customer is always the one in the token. Checkout ignores any `customerId` in the request, and another shopper's basket or order answers `404`, the same as one that does not exist.
- An open basket with no owner can be claimed once with its id (`/claim`). Two shoppers claiming at once leaves one owner.

### Existing customers and password reset

**Registration is refused for any email that already has a customer record**, whether made by staff or by an earlier order. Letting a stranger attach a password to it would hand them that customer's order history.

Such a customer gets a sign-in the safe way: `POST /auth/forgot-password` emails a single-use link, and `POST /auth/reset-password` with it sets the password (creating the sign-in if there is none). Receiving the email is the proof of ownership. It also rescues someone whose address was registered by a stranger, and clears a lockout.

Reset links:

- work once, expire (default 60 minutes), and die when another is used,
- are stored only as a hash,
- are limited to 5 emails an hour per customer, and
- never reveal whether an address belongs to a customer.

Set `PiranhaCommerce:Customers:ResetUrlTemplate` (for example `https://shop.example.com/reset?email={email}&token={token}`) so the email carries a link to your storefront page. Without it the email carries the code.

## Verified email

Turn on `PiranhaCommerce:Customers:RequireVerifiedEmail` (off by default) to make shoppers prove their address.

- **Registering** returns `202` with no token, and emails a link (template `customer.verify-email`, page set by `VerifyUrlTemplate`, lasting `VerifyTokenMinutes`, default a day).
- **Verifying:** the shopper posts the code to `/storefront/auth/verify-email`. They can ask for another at `/auth/resend-verification`, which is silent and has its own hourly limit, counted apart from password resets.
- **Until then**, a correct password is answered `403 email_not_verified`. A wrong password or unknown address is the usual `401`, so it tells nobody that an address has an account.
- Verification links and reset links are not interchangeable.
- Setting a password from an emailed link also verifies the address (receiving it is the proof), and staff can vouch for an address in the Sign-in card.
- A staff edit that changes the address clears the verification and ends the sessions, because the new address is unproven.
- Sign-ins that existed when this was added count as verified, so turning the option on locks no one out. With it off, new shoppers are simply recorded as unverified.

## Two-factor sign-in

A shopper can add a second step to signing in: a six-digit code from an authenticator app (Google Authenticator, Microsoft Authenticator, Authy and the like). It is optional for each shopper. It is separate from staff two-factor, which uses the staff login.

| Setting | Default | What it does |
| --- | --- | --- |
| `PiranhaCommerce:Customers:RequireTwoFactor` | `false` | `true` makes it compulsory: a shopper without it is taken through setting it up the next time they sign in with a password. |
| `PiranhaCommerce:Customers:TwoFactorIssuer` | `Shop` | The name the authenticator app shows beside the code. Set it to your shop's name. |

**Setting it up** (signed in, under `/me/two-factor`): `POST /setup` returns the key and an `otpauth://` address (draw it as a QR code), then `POST /enable` with a code from the app turns it on and returns **ten recovery codes, shown only once**. Until a code is checked it is not on, and starting again within half an hour keeps the same key. `GET /me/two-factor` says whether it is on, how many recovery codes are left, and whether the shop requires it.

**Signing in.** `POST /storefront/auth/token` takes an optional `code`. For an account with two-factor on and no code, the answer is `401` with `error: two_factor_required`, said only after a correct password, so it tells an attacker nothing without it. Send the request again with the code. A code from the app works once (the same code cannot be replayed), and a recovery code works once, typed in any case with or without its dash. A wrong code counts toward the same lockout as a wrong password, and a correct password on its own does not clear that count.

**When the shop requires it.** A shopper without two-factor gets `403` with `error: two_factor_setup_required` and a `setupToken` instead of a session. The token lasts ten minutes, works once, and is good for nothing else. They post it to `/storefront/auth/two-factor/setup` to get the key, then to `/storefront/auth/two-factor/enable` with a code. That turns it on, signs them in and returns the recovery codes with their token. A required shop does not let a shopper turn it off.

**Turning it off, and new recovery codes.** `POST /me/two-factor/disable` needs the password and a current code. `POST /me/two-factor/recovery-codes` needs a current code and replaces all ten.

**If they lose their phone and their codes**, staff turn it off on the Sign-in card ("Turn off two-factor", for anyone with `customers.manage`) or with `POST /customers/{id}/sign-in/reset-two-factor`. That is recorded in the sign-in history as `two-factor-reset`. Only do it once you are sure who you are talking to. The shopper can then set it up again.

The key is stored in the database, so protect backups as you do the rest of the customer data. Recovery codes are stored only as hashes. The customer export says whether two-factor is on, never the key or the codes, and erasing a customer deletes both.

The Petalwick sample shop has the whole thing. The sign-in page asks only for email and password. When the password is right and the account has two-factor on, the shopper is taken to a second page for the code, so shoppers without it never see a code box. There is also a Sign-in security page at `/account/security` with the QR code and recovery codes, and the required-setup page.

**Asking for the code on a second page** (for a server-rendered shop like the sample). `ICustomerAccountService.SignInAsync` returns `TwoFactorRequired` with a `Challenge` for a correct password. It is good for five minutes, holds no password, and a newer one replaces the older. Pass it with the code to `ICustomerTwoFactorService.CompleteChallengeAsync`. A wrong code counts toward the lockout and leaves the challenge usable, and a right one spends it. The storefront API does not need this: it is stateless, so the app just sends the request again with `code`.

## Social sign-in

The optional package `PiranhaCommerce.Customers.SocialSignIn` lets shoppers sign in with Google, Microsoft, Apple or any OpenID Connect provider. Register it with `.AddPlugin<SocialSignInPlugin>()`; it needs Customers, has its own table (add a migration), and does nothing until a provider is configured. Facebook is not supported, because it does not give an ID token that can be verified the same way.

**How it works.** Your app (a mobile app, a single-page app, or your own server) signs the shopper in at the provider and gets an **ID token**. It sends that to `POST /storefront/auth/external/{provider}` and gets the shop's own token back. The shop checks the token itself: the signature against the provider's published keys (asymmetric algorithms only), the issuer, that the audience is one of **your** client ids, that it has not expired, and the `nonce` if you send one. A token made for someone else's app is refused however genuine it is.

```json
"PiranhaCommerce": { "Customers": { "Social": {
  "AccountMatching": "Never",
  "AllowSignUp": false,
  "Providers": {
    "google": { "DisplayName": "Google", "Authority": "https://accounts.google.com", "ClientIds": [ "1234.apps.googleusercontent.com" ] },
    "apple":  { "DisplayName": "Apple",  "Authority": "https://appleid.apple.com",    "ClientIds": [ "com.example.shop" ] }
  } } } }
```

`Authority` is the provider's OpenID Connect address (it must be https, with `/.well-known/openid-configuration` under it). `Issuer` is only needed where a provider's tokens carry a different one. The name (`google`) is what appears in the URL. `GET /storefront/auth/external` lists what the shop offers, for your buttons.

**Linking is by the provider's own account id, never by email.** A shopper's Google account can be on a different email from the one they shop with, and that is fine. They sign in to the shop as usual, then add the social account from their profile: `POST /storefront/me/external/{provider}` with the ID token. Nothing about the two emails is compared. From then on the ID token signs them in, even if the email at the provider changes. `GET /me/external` lists what is linked and `DELETE /me/external/{provider}` removes one (they can always choose a password with "forgot password"). One social account belongs to one shop account, and a shopper has one account per provider.

**A social account that is not linked yet** is handled by two settings:

| Setting | Default | What it does |
| --- | --- | --- |
| `AccountMatching` | `Never` | `Never`: an unlinked social account never reaches an existing shop account, even with the same email. The answer is `409 account_exists`, which tells them to sign in and add it from their profile. `LinkVerifiedEmail`: if the provider says the email is verified, link it to the shop account with that address. |
| `AllowSignUp` | `false` | `true`: someone with no account can make one by signing in with a social account. The provider must say the email is verified, or the answer is `403 email_not_verified_by_provider`. `false`: the answer is `403 sign_up_disabled`. |

`Never` is the safe choice. `LinkVerifiedEmail` is convenient, but it means anyone who controls the social account can reach the shop account with that address, so a compromised Google account becomes a compromised shop account. If you turn it on, know that when the shop account was registered **without** a verified email, its password is wiped and its sessions end, because whoever registered it first may have been a stranger who never owned the address.

**The shop's other rules still apply.** A shopper with two-factor on is asked for their code after a social sign-in (`401 two_factor_required`, then send the same ID token again with `code`), and with `RequireTwoFactor` they are taken through setup (`403 two_factor_setup_required`) like anyone else. Switched-off customers, lockout and `RequireVerifiedEmail` all apply. A stolen social login alone gets nobody in whose account is protected by two-factor.

**For staff.** The optional package `PiranhaCommerce.Customers.SocialSignIn.Admin` adds a "Linked social accounts" panel to the customer page, showing each provider, the email at the provider, when it was linked and last used, with an Unlink button for anyone with `customers.manage`. The API is `GET /customers/{id}/social` (needs `customers.view`) and `DELETE /customers/{id}/social/{provider}` (needs `customers.manage`). Unlinking is recorded in the sign-in history as `social-unlinked`, with who did it.

**In the Petalwick sample shop** the sign-in page shows Google's own "Sign in with Google" button whenever a `google` provider is configured, and the Sign-in security page lets a signed-in shopper add or remove a Google account. The button runs in the browser and posts Google's ID token to the shop in a form, so the shop needs no script of its own. The shop asks Google to put a one-time `nonce` in the token and checks it, and checks Google's double-submit `g_csrf_token` cookie. A shopper with two-factor is taken to a page asking for their code. The route, `/account/social/{provider}`, uses `ISocialSignInService`, which is public so a server-rendered shop of your own can do the same. For other providers, use their sign-in button the same way and post the ID token to `/account/social/{provider}`.

Accounts made by social sign-up have a random password nobody knows. They can choose one with "forgot password". The sign-in history records `social-linked`, `social-unlinked` and `social-sign-in`, linked accounts are in the customer export, and erasing a customer removes them. The sign-in routes share the shop's sign-in rate limit.

## Shopper sign-ins, for staff

Staff manage a customer's sign-in on the Sign-in card on the customer page (and a Sign-in column in the list), or at `/customers/{id}/sign-in`. They can see whether a customer has no sign-in, can sign in, is locked out or is switched off, and they can:

- **unlock** a lockout,
- **sign out everywhere**,
- **send a reset link**. For a customer with no sign-in, such as one staff made, this is the invitation: the link creates it, the address being the proof. Staff are told what happened: sent, customer switched off, hourly limit reached, or email provider failed.
- **remove the sign-in**. The customer, addresses and orders stay, and any link already emailed is spent.

**Sessions end at once**, not after the token's hour. Every shopper token carries the sign-in's session version, which is checked on every storefront call along with the customer being active and the sign-in existing. Signing out everywhere, switching the customer off, removing the sign-in, changing the password and using a reset link each end every token.

Changing the password returns a fresh token (the response is `200` with a token, not `204`) so the device that changed it stays signed in. Tokens issued before versions existed carry no version and count as 0. The password hash and the version are never in any response.

Every staff action (unlock, sign out, reset link with its outcome, removal, switching off or on) is recorded with who did it and when. It is shown in the card and at `GET /customers/{id}/sign-in/history`, included in the customer's export, and deleted with them on erasure. Customers' own sign-ins and password changes are not logged.

## Customer groups

Groups are named sets of customers (trade, staff, VIP) that staff fill and other features use. They are at `/customer-groups`, with `/customer-groups/{id}/members/{customerId}` (`PUT` adds, `DELETE` removes) and `/customers/{id}/groups`. In the admin: Customers > Groups, and a panel on each customer.

- Names are unique whatever the letter case. Adding someone twice is harmless.
- Deleting a customer or a group removes the memberships; the customers stay.
- A group cannot be deleted while a promotion is limited to it.
- A promotion can be limited to a group (`customerGroupId`): only signed-in customers in it can apply the code, and everyone else is told the same "That code is not valid." as for any wrong code. It is checked again at checkout, so leaving the group after applying the code stops the order (`409`) without using up the code.
- A customer's export lists their groups.

Groups can also carry prices: see [Prices, tax and discounts](pricing.md).

## Shoppers' own privacy

A signed-in shopper can fetch everything held about them and erase their own account.

- `GET /storefront/me/export` returns the same export staff see, **including staff-only notes**: it is their data, and a formal subject-access request may require it. Change that here if your legal advice differs.
- `POST /storefront/me/erase` needs the password again. It is checked and counted towards the lockout like a sign-in, so a stolen token cannot wipe an account.
- Erasure follows the same rules as staff erasure (below): orders are anonymised, never deleted, and it is refused, with the order references, while any order is open or a refund is owed. The shopper is told to contact the shop.
- The erasure record names the actor "customer (self-service)".
- Success is `204` and the token stops working. A storefront needs its own confirmation screen.

## Customer data export and erasure

Staff use `GET /customers/{id}/export` and `POST /customers/{id}/erase`, or the Privacy card on a customer in the admin.

### Export

Staff can see everything held about a customer: their record including staff notes, their address book, whether they can sign in (never the password hash), and each package's part. For the store that means their orders and open baskets.

### Erasure

Erasing needs the customer's email typed as confirmation and runs in one transaction.

**Deleted:** the customer record, address book, sign-in and reset links. Open baskets are deleted, and finished ones let go of the person.

**Kept, anonymised:** orders are **never deleted**, because the shop must keep its accounts. The buyer's name, address lines, region, postcode, email and notes are replaced (the email by the undeliverable `erased@erased.invalid`), while totals, items, payments, shipments and the **country** stay. This covers orders on the account and guest orders under the same email address, in any letter case.

**Cleared text:** notes staff typed on manual payments and on returns are cleared. (The payment ledger is otherwise append-only, so this is the one place its text is edited.) Return reasons are kept, so do not write personal data in them.

**Refused** while any of their orders is Placed or Processing, or is cancelled with money not yet refunded (their contact details are the only way to return it). The refusal lists the reasons.

**Afterwards:** nothing is emailed about an erased order, even when a late refund arrives. The SMTP sender also refuses any address on the reserved `.invalid` domain before connecting. An email sender you write should check `UndeliverableAddress.IsUndeliverable`, since a send can cost money. A record of each erasure (the old id, when, by whom, counts) is kept, with no personal data in it.

A package that holds personal data takes part by registering an `ICustomerDataContributor` (check, export, erase).

### What erasure does not reach

- copies held by your payment provider (Stripe keeps the checkout email),
- your email provider (send logs),
- server logs and backups.

`DELETE /customers/{id}` is **not** erasure: it leaves their orders as they were.

**Known gap:** a checkout that lands at the very moment its customer is being erased can create an order with the real email after the anonymising has run. Erase again once that order has been cancelled or completed.
