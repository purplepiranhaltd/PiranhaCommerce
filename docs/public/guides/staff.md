---
title: Staff and permissions
order: 95
description: Roles, what each permission allows, the staff page, and how a package adds its own permission.
---

This guide covers who on your team can do what. Every member of staff has one or more **roles**, and a role is a list of **permissions**. The admin and the API both check them on every call.

## Roles

- **Administrator** is built in. It can do everything, including anything a package adds later, and it cannot be edited or deleted.
- **Everything else is a role you make.** Open **Staff > Staff and roles** in the admin, choose **New role**, name it and tick what it may do. **Add suggested roles** creates four starting points you can change: Fulfilment, Catalogue editor, Customer service and Viewer.
- **No role means nothing.** Someone with no role can sign in but sees no menu and every page says they are not allowed. This is deliberate: new staff start with nothing.
- **A role with people in it cannot be deleted.** Move them first.
- **There is always an administrator.** The last active administrator cannot be switched off, removed or demoted.

Changes take effect within seconds, without the person signing in again, because permissions are looked up from the database on each request and never stored in the sign-in token. Switching someone off cuts them off at once.

## The permissions

| Permission | What it allows |
|---|---|
| `catalog.view` | See products, categories, brands, stock, discounts, sales and promotions. |
| `catalog.edit` | Change products, categories, brands, tags, specifications, images, variants and imports. |
| `prices.manage` | Change prices, price breaks, group prices, discounts, sales and promotions. |
| `stock.adjust` | Adjust stock and manage stock locations. |
| `stock.transfer` | Send, receive and cancel stock transfers. |
| `orders.view` | See orders, vouchers, disputes, return requests and unreconciled payments. |
| `orders.create` | Place an order on a customer's behalf. |
| `orders.fulfil` | Move an order through its stages, ship it, record a payment or a return, and decide return requests. |
| `orders.refund` | Refund, handle disputes and resolve unreconciled payments. |
| `vouchers.manage` | Issue and change gift vouchers. |
| `customers.view` | See customers, groups, reviews and wishlists. |
| `customers.manage` | Change customers, groups and sign-in state; erase a customer. |
| `reviews.moderate` | Approve, hide, reply to and delete reviews. |
| `settings.manage` | Shipping, tax and email templates. |
| `reports.view` | Reports. |
| `audit.view` | The audit log. It is separate because it shows who did what. |
| `webhooks.manage` | Webhook endpoints and deliveries. |
| `staff.manage` | The staff page, roles, and which stock locations each person works at. |

Some permissions include another, so you do not need to tick both: editing the catalogue, adjusting or transferring stock, and managing prices all include seeing the catalogue; creating, fulfilling or refunding orders and managing vouchers include seeing orders; managing customers and moderating reviews include seeing customers.

Reading is controlled too. Someone with only `orders.fulfil` sees orders but not the catalogue, customers or reports. The shipping, tax and email-template pages are visible to any member of staff who has at least one permission, but only `settings.manage` can change them. A page or endpoint that names no permission at all is for administrators only.

## The API

The API enforces the same permissions. Call `POST /api/v1/auth/token` as the person and the token works for exactly what their roles allow, as they change. A call without the permission returns `403`. An endpoint a package adds without naming a permission is treated as administrator-only, so a forgotten annotation fails closed.

## Two-factor sign-in

Two-factor sign-in is **compulsory for all staff, whatever their role**. The first time someone signs in (including the first administrator) the admin takes them to **My account > Sign-in security**, and that is the only page open to them until they have set it up: choose **Set it up**. Scan the QR code with an authenticator app on your phone (or type in the key shown beside it), type the six-digit code it shows, and you are given ten recovery codes. Keep them somewhere safe; each works once if you lose your phone, and they are shown only then.

- **At sign-in** the admin asks for the code after the password. The API does the same: if the password is right but the person has two-factor on, `POST /api/v1/auth/token` answers `401` with `{ "error": "two_factor_required", "message": "..." }`; send the request again with `"code": "123456"` (a recovery code works too). For an integration, use an API key instead, which has no code to type.
- **Guessing is limited.** A wrong code counts towards the same lockout as a wrong password, and a correct password on its own does not clear that count.
- **It cannot be turned off** while it is compulsory. Making new recovery codes needs a current code, so someone at an unlocked screen cannot do it.
- **The API** gives a person no token until they have set it up in the admin: `POST /api/v1/auth/token` answers `403` with `{ "error": "two_factor_setup_required", "message": "..." }`. The message says what to do: sign in to the admin and set up an authenticator app, or, for local development only, set `PiranhaCommerce:Staff:RequireTwoFactor` to `false`.
- **API keys are not people** and are not affected.
- **For local development** set `PiranhaCommerce:Staff:RequireTwoFactor` to `false` (the sample's `appsettings.Development.json` does). Leave it on everywhere else; it is on by default.
- **A lost phone:** use a recovery code, or ask an administrator to choose **Reset two-factor** next to your name on **Staff > Staff and roles**. That discards the old key and recovery codes and records the reset in the audit log. At your next sign-in you set it up again on the new phone. An administrator can never see or set anyone's codes.
- **Shoppers** cannot use it yet.

## API keys

A key lets another system (a warehouse system, an accounts package) call the API without a person's password. Open **Staff > API keys**, say what the key is for, tick the roles it acts with and choose **Make key**. The key is shown once; copy it then, because only a fingerprint is kept and it cannot be read again.

- **Use it** as `Authorization: Bearer pck_...` or as an `X-Api-Key: pck_...` header, on the same `/api/v1` endpoints a person uses. It can do what its roles allow and nothing else: a key with no role can do nothing. Give each integration its own key and the least it needs.
- **It is not a person.** It cannot sign in to the admin, it does not appear in the staff list, and it does not count as an administrator for the "at least one administrator" rule.
- **Changes are recorded under its name** in the audit log.
- **Revoke it** when it is lost or no longer needed: it stops working at once and cannot be switched back on, so make a new one. A key can also be given an expiry date.
- **Changing the key's roles** takes effect on the next call.
- **Storefront calls are not for keys.** A key cannot act as a shopper.

## Adding a permission from a package

Declare it with an `IPermissionContributor`, state what it implies, then use it on your endpoints, pages and services. It then appears in the role editor under your group with no other registration.

```csharp
public sealed class WarrantyPermissions : IPermissionContributor
{
    public IEnumerable<Permission> GetPermissions() =>
    [
        new("warranty.edit", "Edit warranties", "Catalogue", "Set the warranty on a product.", Implies: [Permissions.CatalogView]),
    ];
}
```

- **API:** `group.RequirePermission("catalog.view")` on the group, and `.RequirePermission("warranty.edit")` on the write endpoints.
- **Admin page:** `@attribute [Authorize(Policy = PermissionPolicies.For("warranty.edit"))]`. A page that names none is for administrators only.
- **Admin menu:** `new AdminMenuItem(..., RequiredPermission: "warranty.edit")`. The entry is hidden from people without it, and a section whose entries are all hidden disappears. Hiding a link is only a courtesy; the page and the service both check as well.
- **Buttons:** wrap an action in `<Can Permission="warranty.edit">...</Can>` (from `PiranhaCommerce.Admin.Components`) to hide it from people who could not use it. The built-in pages do this for every action that changes something: add, edit, delete and save on each list and panel, order actions, refunds, stock, vouchers, reviews, shipping, tax, email templates and webhooks. Buttons that only look (search, paging, history, downloads) stay visible.
- **Service methods:** mark each method that changes something with `[RequiresPermission("warranty.edit")]` on the interface. The admin refuses the call for anyone without it, whichever page made it. Methods that only read need no mark.

The built-in permission keys are constants on `Permissions`, and the matching policy names are constants on `PermissionPolicies`.

## The audit trail

When the audit package is installed, creating, changing or deleting a role, giving someone a role and switching someone on or off are all recorded, with who did it.
