# Vocabulary

The names PiranhaCommerce uses. Check new names against this list before adding them. Words every merchant already uses
(Customer, Order, Checkout, Category, Product) stay; constructs that are specific to another platform get different names.

| Concept | Name used | Notes |
|---|---|---|
| Sales channel | **Channel** | Ecommerce and EPOS are channels. There is no "Store" entity. |
| Shopping cart | **Basket**, **BasketLine** | A wishlist, if added, is a separate entity. |
| Order | **Order**, **OrderLine** | One `Stage` lifecycle; payments are their own records. |
| Customer segmentation | **CustomerGroup** | Pricing/segmentation only. Permissions use ASP.NET Identity roles. |
| Warehouse / shop floor | **StockLocation** | One default location exists; single-site shops never need to see it. |
| Typed configuration | **Options** | .NET Options pattern, not a key/value settings table. |
| Discount / coupon | **Promotion**, **PromoCode** | |
| Gift card | **GiftVoucher** | |
| Shipment | **Fulfillment**, **Parcel** | |
| Vendor / manufacturer | **Supplier**, **Brand** | |
| Product attributes | **Variant**, **VariantAxis**, **VariantValue** | |
| Product facts shoppers compare / filter by | **SpecAttribute**, **SpecValue** | Fixed lists, unlike tags. |
| Free-form product labels | **Tag** | Made as they are used. |
| Content pages | **Page** | |
| Extension point in the admin | **Slot** (`AdminSlots`) | |
| Plugin dependencies | **Requires** | |

## Conventions

- **Spelling:** US spelling in code, API and database names (`Fulfillment`, `Color`), matching the .NET base libraries. UK spelling
  in admin UI text and documentation.
- **Customers are not admin users.** `PiranhaUser` is an administrator identity; a shopper is a `Customer`, optionally linked to
  a storefront login later.
- **Languages:** the admin UI and storefront must be translatable. User-facing text stays out of the data model so translation
  does not need migrations. Not built yet.
- **Stock** is shared, in the Catalog package, so every channel uses the same ledger.
