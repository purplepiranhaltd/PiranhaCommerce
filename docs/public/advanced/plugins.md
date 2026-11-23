---
title: The plugin model and writing a plugin
order: 10
description: How packages plug in, and how to write your own.
---

## How plugins work

Plugins are NuGet packages referenced at build time and registered in `Program.cs`:

```csharp
builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(connectionString)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();
```

A plugin lists the plugins it needs in `Requires` (typed instances from packages it references). They are registered
automatically, once and first, so the host above gets Catalog and Catalog.Admin without naming them.
Channel-specific product data goes in the channel's own table keyed by `ProductId` (cascade delete), and its admin UI
joins the product page through an admin slot.

There is no runtime upload or assembly scanning: only code built by the dev team runs. A plugin registers
`IModelContributor`, `IApiEndpointContributor`, `IAdminMenuContributor` and `IAdminSlotContributor` services.

## Writing a plugin

`samples/PiranhaCommerce.Example.Warranty` is a complete third-party plugin in one project, touching nothing in core. It adds a
product warranty: an entity in its own schema (`IModelContributor`), API endpoints (`IApiEndpointContributor`) and an admin panel
on the product edit page (`IAdminSlotContributor` into `AdminSlots.ProductEditPanels`). Steps for your own:

1. Implement `IPiranhaPlugin`; list plugins you depend on in `Requires` (they are registered first, once).
2. Reference the plugin's package from the host and call `.AddPlugin<YourPlugin>()`.
3. **Add a migration in the host** (`dotnet ef migrations add AddYourPlugin`, once per provider). The host owns the schema, so a new
   plugin shows up as a pending model change until you do, and CI's drift check will tell you.
4. Give Razor components an explicit `@namespace` so they do not depend on the project they are compiled into.

Removing a plugin is also a host migration: remove the registration, then add a migration that drops its tables.
