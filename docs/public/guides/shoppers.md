---
title: Customers and shopper accounts
order: 20
description: Customers, sign-in and tokens, groups, verified email, privacy, export and erasure.
---

## Customers

`PiranhaCommerce.Customers` holds people and businesses that buy, for every channel. A customer has an email (required, unique
whatever the letter case, enforced by a database index), optional name, phone and staff-only notes, and an address book of up to
50 addresses (country as an ISO two-letter code). Orders copy the address they use, so editing an address never changes
history. API: `/api/v1/customers` and `/api/v1/customers/{id}/addresses` (reads need the read policy, changes the write policy).

A customer is deliberately not an admin user (see "Shopper accounts" below).

## Shopper accounts (storefront API)

Shoppers sign in separately from staff. Under `/api/v1/storefront`:

- `POST /auth/register`, `POST /auth/token` (anonymous). Registration returns a token; a wrong password, unknown email, locked or
  deactivated account all answer the same `401`. 5 wrong passwords lock the account for 15 minutes (`Piranha:Customers`).
- **Refresh tokens** (`POST /auth/refresh`, `POST /auth/logout`, both with `{ "refreshToken" }`): signing in, registering and changing a password also return a
  `refreshToken`. Swap it at `/auth/refresh` for a new access token and the next refresh token (each is single use; the response is the same shape as
  signing in). Only a hash is stored. Using a token a second time means it was copied, so that device's whole chain is revoked (note that two refreshes at
  the same moment look like this, so an app should refresh from one place). A refresh token stops working when sessions are ended (sign out everywhere,
  password change or reset, switching the customer off, a staff email change), after `Piranha:Customers:RefreshTokenDays` (default 30, counted from each
  use; 0 switches refresh tokens off) and after `RefreshTokenMaxDays` (default 180) since that sign-in began. Anything not live is the same `401`.
  `/auth/logout` ends one device's chain (always `204`). Refresh tokens are for the shopper API only, never staff.
- **Their devices:** `GET /me/sessions` lists the devices that can still refresh (id, a readable `device` such as "Chrome on Windows" taken from the
  User-Agent when they signed in, `current` for the device making the call, started, last used, expires). The User-Agent is cleaned and cut to 200 characters; no IP address is kept. A shop's own app can send a User-Agent like `MyShop/2.1` and that is shown as it is; `DELETE /me/sessions/{id}` ends one
  (`404` if it is not theirs). `POST /me/sign-out-everywhere` ends every session, access tokens included, and returns a new token pair for the device that
  asked. It is recorded in the sign-in history as done by the shopper.
- `GET|PUT /me`, `POST /me/password`, `GET|POST|PUT|DELETE /me/addresses` - the signed-in shopper's own record only. Staff notes are never shown.
- `POST /baskets`, `GET /baskets/{id}`, `PUT|DELETE /baskets/{id}/lines/{productId}`, `POST /baskets/{id}/quote`,
  `POST /baskets/{id}/checkout`, `POST /baskets/{id}/claim`, `GET /orders`, `GET /orders/{id}`.

How it is kept apart from the staff API:

- A shopper's sign-in lives in its own table, not among the admin users, so a customer can never open the admin or hold a role.
- Shopper tokens have their own audience, signing key (derived from `Piranha:Jwt:SigningKey`) and scheme. The staff API refuses them
  and the storefront refuses staff tokens. Every storefront endpoint requires a shopper token unless it says otherwise.
- The customer is always the one in the token. Checkout ignores any `customerId` in the request, and another shopper's basket or
  order answers `404`, the same as one that does not exist.
- **Registration is refused for any email that already has a customer record** (made by staff or by an earlier order). Letting a
  stranger attach a password to it would hand them that customer's order history. Such a customer gets a sign-in the safe way:
  `POST /auth/forgot-password` emails a single-use link, and `POST /auth/reset-password` with it sets the password (creating the
  sign-in if there is none). Receiving the email is the proof of ownership. It also rescues someone whose address was registered
  by a stranger, and clears a lockout.
- Reset links work once, expire (default 60 minutes), die when another is used, are stored only as a hash, and are limited to 5
  emails an hour per customer. Asking never reveals whether an address is a customer. Set `Piranha:Customers:ResetUrlTemplate`
  (for example `https://shop.example.com/reset?email={email}&token={token}`) so the email carries a link to your storefront page;
  without it the email carries the code.
- A deactivated customer is cut off at once, even with an unexpired token. Tokens last `Piranha:Jwt:LifetimeMinutes` (60); changing a
  password does not end tokens already issued.
- An open basket with no owner can be claimed once with its id (`/claim`); two shoppers claiming at once leaves one owner.

## Verified email

(`Piranha:Customers:RequireVerifiedEmail`, off by default): when on, registering returns 202 with no token and emails a link
(template `customer.verify-email`, page set by `VerifyUrlTemplate`, lasts `VerifyTokenMinutes`, default a day); the shopper posts it to
`/storefront/auth/verify-email` and can ask for another at `/auth/resend-verification` (silent, hourly limit counted apart from password
resets). Until then a correct password is answered 403 `email_not_verified`; a wrong password or unknown address is the usual 401, so it tells nobody
that an address has an account. Verification links and reset links are not interchangeable. Setting a password from an emailed link also verifies the
address (receiving it is the proof), and staff can vouch for one in the Sign-in card. A staff edit that changes the address clears the verification and
ends the sessions (the new address is unproven). Sign-ins that existed when this was added count as verified, so turning the option on locks no one
out; with it off, new shoppers are simply recorded as unverified.

## Shoppers' own privacy

(`GET /storefront/me/export`, `POST /storefront/me/erase`): a signed-in shopper can fetch everything held about
them (the same export staff see, including staff-only notes: it is their data, and a formal subject-access request may require it; change that
here if your legal advice differs) and erase their own account. Erasing needs the password again (checked and counted towards the lockout like
a sign-in, so a stolen token cannot wipe an account) and follows the same rules as staff erasure: orders are anonymised, never deleted, and it is
refused with the order references while any order is open or a refund is owed (they are told to contact the shop). The erasure record names the
actor "customer (self-service)". Success is 204 and the token stops working. A storefront needs its own confirmation screen.

## Customer data export and erasure

(`GET /customers/{id}/export`, `POST /customers/{id}/erase`, admin: the Privacy card on a customer):
staff can see everything held about a customer (their record including staff notes, address book, whether they can sign in but never the
password hash, and each package's part: for the store, their orders and open baskets) and erase them. Erasing needs the customer's email typed
as confirmation and runs in one transaction. The customer record, address book, sign-in and reset links are deleted. **Orders are never
deleted** (the shop must keep its accounts): the buyer's name, address lines, region, postcode, email and notes are replaced (the email by the
undeliverable `erased@erased.invalid`) while totals, items, payments, shipments and the **country** stay. That covers orders on the account and
guest orders under the same email address (any letter case). Notes staff typed on manual payments and on returns are cleared (the payment ledger
is otherwise append-only, so this is the one place its text is edited); return reasons are kept, so do not write personal data in them. Open baskets
are deleted and finished ones let go of the person. Erasure is refused, with the reasons, while any of their orders is Placed or Processing or
is cancelled with money not yet refunded (their contact details are the only way to return it). Nothing is emailed about an erased order, even
when a late refund arrives, and the SMTP sender also refuses any address on the reserved `.invalid` domain before connecting (an email sender
should check `UndeliverableAddress.IsUndeliverable`, since a send can cost money). A record of each erasure (the old id, when, by whom, counts) is kept with no personal data in it. A package that
holds personal data takes part by registering an `ICustomerDataContributor` (check, export, erase). **What this does not reach:** copies held
by your payment provider (Stripe keeps the checkout email), your email provider (send logs), server logs and backups. Deleting a customer with
`DELETE /customers/{id}` is not erasure: it leaves their orders as they were. Known gap: a checkout that lands at the very moment its customer
is being erased can create an order with the real email after the anonymising has run; erase again once it has been cancelled or completed.

## Shopper sign-ins, for staff

(`/customers/{id}/sign-in`, admin: the Sign-in card on a customer and a Sign-in column in the list): see whether a
customer has no sign-in, can sign in, is locked out or is switched off; **unlock** a lockout; **sign out everywhere**; **send a reset link** (for a
customer who has no sign-in, such as one staff made, this is the invitation: the link creates it, the address being the proof); and **remove the
sign-in** (the customer, addresses and orders stay, and any link already emailed is spent). Sending a link tells staff what happened (sent, customer
switched off, hourly limit reached, email provider failed). Sessions end at once, not after the token's hour: every shopper token carries the
sign-in's session version, which is checked on every storefront call along with the customer being active and the sign-in existing. Signing out
everywhere, switching the customer off, removing the sign-in, changing the password and using a reset link each end every token. Changing
the password returns a fresh token (the response is 200 with a token, not 204) so the device that changed it stays signed in. Tokens issued before
this existed carry no version and count as 0. The password hash and the version are never in any response. Every staff action (unlock, sign out, reset link
with its outcome, removal, switching off or on) is recorded with who did it and when, shown in the card and at `GET /customers/{id}/sign-in/history`,
included in the customer's export, and deleted with them on erasure. Customers' own sign-ins and password changes are not logged.

## Customer groups

(`/customer-groups`, `/customer-groups/{id}/members/{customerId}` (PUT adds, DELETE removes), `/customers/{id}/groups`; admin: Customers >
Groups and a panel on each customer): named sets of customers (trade, staff, VIP) that staff fill and other features can use. Names are unique whatever the letter
case; adding someone twice is harmless; deleting a customer or a group removes the memberships (the customers stay). A group cannot be deleted while a
promotion is limited to it. A promotion can be limited to a group (`customerGroupId`): only signed-in customers in it can apply the code, and everyone
else is told the same "That code is not valid." as for any wrong code. It is checked again at checkout, so leaving the group after applying the code stops the
order (409) without using up the code. A customer's export lists their groups. Groups hold no prices: group pricing is not built.
