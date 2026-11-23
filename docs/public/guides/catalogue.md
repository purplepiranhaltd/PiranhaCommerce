---
title: Catalogue and stock
order: 10
description: Products, categories, barcodes, pictures, variants, stock, locations, transfers and import/export.
---

This guide covers what you sell and where it is held. All of it lives in the shared `PiranhaCommerce.Catalog` package, which every sales channel uses (the online store, and any other channel you add). Each feature has an API under `/api/v1` and a page in the admin. API errors are `400` for invalid input and `409` for a conflict with the data as it stands.

## Categories

Categories form a tree: each has an optional parent (`ParentId`). A product can be in any number of categories.

- Cycles are refused.
- A category that has sub-categories cannot be deleted (`409`).
- `PUT /products/{id}/categories` replaces the product's whole set of categories.

## Slugs and page text

Products and categories have a **slug** (lower-case letters, digits and single hyphens; unique; lower-cased for you), a `metaTitle` and a `metaDescription`. A shop front that routes by address uses `GET /products/by-slug/{slug}` and `GET /categories/by-slug/{slug}`.

A `PUT` replaces these fields, so leaving one out clears it. Not built: a history of old slugs with redirects, and translated slugs.

## Barcodes

A product can have several barcodes. GTIN-8, 12, 13 and 14 codes are checked for a correct check digit and stored left-padded to 14 digits, so a UPC-A code and its EAN-13 form are the same barcode and cannot belong to two products.

Look a product up with `GET /products/lookup?barcode=...`. The barcode is a query value, not part of the path, because Code 128 values can contain `/`.

## Product pictures

Pictures are on a product's Pictures panel in the admin, or under `/products/{id}/images`. They need a storage package (`IFileStore`); the sample host uses local disk.

**Uploading.** Send `multipart/form-data` with `file` and an optional `altText`. The type is decided from the file's own bytes: JPEG, PNG, WebP or GIF. SVG is refused because it can carry script. Limits are `Piranha:Images:MaxBytes` (10 MB) and `Piranha:Images:MaxPerProduct` (20).

**What happens to the file.** With an imaging package installed, the picture is read (a file that cannot be read is refused), turned upright using its camera orientation data, and smaller copies are made (`Piranha:Images:Sizes`, default `160,480,1024` on the longest side, never enlarged).

**Managing pictures.** The first picture is the main one. `PUT .../images/order` sets the order, `PUT .../images/{id}` changes the alt text, and `DELETE` removes the picture and its files. Deleting a product removes all of its pictures. A variant with no pictures of its own returns its parent's, marked `inherited`.

**Showing them.** A shop front fetches a picture with no sign-in, at the `url` in its description. `GET /api/v1/storefront/guest/product-images/{id}?width=300` returns the smallest made size at least that wide (or the original), with an `ETag` and a day of caching.

A picture is found by its own unguessable id, so it stays reachable when its product is unpublished. Treat the address as the secret.

Not built: cropping, watermarks, video, bulk upload, and resizing for pictures uploaded before an imaging package was added.

## Options and variants

A product that comes in options (Size, Colour, Shade) has **options** with their values, and a **variant** for each combination. They are under `/products/{id}/options` and `/products/{id}/variants`, and in the admin on the product's Options and variants panel.

Create the options, then make the variants all at once (`POST .../variants/generate`) or one at a time (`POST .../variants`, with one value per option).

**A variant is an ordinary product.** It has its own SKU, price, stock, barcodes and listing, and its name is the parent's plus its values ("Tee - M / Blue"). Because of that, stock, baskets, orders, sales and group prices need no special cases for variants.

**The parent is not for sale.** The product that holds the options is the parent. It carries the shared name, description and categories, and it cannot be bought itself: adding it to a basket is refused, so add a variant.

**Rules**
- At most 3 options, 50 values per option and 200 variants.
- These are refused with `409`: removing a value or option that is in use, deleting a parent that has variants, adding options to a variant, and adding an option once variants exist.
- A new variant copies the parent's online listing (published, tax class, weight) and its description, but not its categories. A category sale or group category discount reaches variants through the parent's categories.
- A sale or group price that names the parent product does **not** cover its variants. Name the variants, or use a category.

`GET /products/{id}/variants` returns a parent's options and variants, or, for a variant, its parent and values. A variant has its own pictures.

Not built: pricing a variant as an adjustment to the parent's price, renaming values, and variants updating their name when the parent is renamed. A shop front reads variants through the catalogue endpoints described in [Building a shop front](storefront.md).

## Stock

Each product has a quantity (to four decimal places, so goods sold by weight work). The quantity changes only through **adjustments**, and every adjustment is recorded in an append-only ledger (`StockMovement`) with its reason, the user, and the balance afterwards.

- Adjustments are atomic and safe when many happen at once or a request is retried after a transient failure.
- Stock cannot go below zero by default. A caller that must record physical reality, such as an EPOS till selling something the system thought was gone, passes `AllowNegative`.
- A product with stock history cannot be deleted (`409`), because the ledger is an audit trail.

### Locations

Stock is held per **location**: a warehouse, a shop, a van. A default location always exists and is used whenever a caller names none, so a single-site shop never has to think about locations. Locations are deactivated, never deleted. Each movement records the balance at its own location.

### Negative stock report

`GET /stock/negative`, or Catalog > Negative stock in the admin, lists every product and location below zero. It exists for channels such as EPOS that must record a sale even when the system thought it had none. A product drops off the list once a recount brings it back to zero or more.

### Moving stock between locations

Transfers (`/stock/transfers`, admin: Catalog > Stock transfers) move stock in two steps, by two people.

1. **Send.** `POST` creates the transfer. The stock leaves the sending location at once (refused if there is not enough) and the transfer is **in transit**: on neither shelf, but listed at `GET /stock/in-transit` (per product and route) and shown as `inTransit` on a product's stock level.
2. **Receive.** `POST /{id}/receive` adds what arrived to the receiving location. By default a different user must receive it (`Piranha:Stock:AllowSameUserToReceive` allows the same one). To say that less or more arrived than was sent, send `{ "lines": [{ "productId", "quantity" }] }`. The transfer is then flagged (`hasDiscrepancy`; filter the list with `?discrepancies=true`).
3. **Or cancel.** `POST /{id}/cancel` while it is in transit puts the stock back.

Every step is a ledger movement ("Transfer sent", "Transfer received", "Transfer cancelled") carrying the `TRF-` reference and the user. A transfer is received or cancelled exactly once, however many people try at the same moment. The admin menu shows how many are in transit.

### Limiting staff to locations

`GET /staff/locations` and `PUT /staff/{userId}/locations` with `{ "locationIds": [...] }` (admin: Catalog > Staff locations) limit a staff user to certain locations. They can then send, receive, cancel and manually adjust stock only at those; other attempts get `403`. A user with no locations ticked is not limited. The location is named on each action, so there is no choose-a-location step at sign-in.

## Import and export

Catalogue data moves in and out as CSV: `GET /products/export?search=` and `POST /products/import` with a `file` (admin: Catalog > Import and export). **Start from an export**, so your columns and values are right.

### The file

One row per product. The columns are:

`id, sku, parent_id, parent_sku, name, description, price, slug, meta_title, meta_description, categories, barcodes, stock, option1_name, option1_value`, and the same pair for options 2 and 3.

The online store adds `published, tax_class, weight_grams`. A package adds its own columns with `IProductImportExtension`. A column the importer does not know refuses the file.

### How rows are read

| Rule | Detail |
|---|---|
| Matching | A row is matched by `id`, else by `sku`. It is updated if it exists, otherwise created (a new product needs a name and a price). |
| Blank cells | A **blank cell leaves the field alone**. Write **`[clear]`** to empty it. |
| Missing columns | A column that is not in the file is not touched. |
| Variants | A variant is a row with `parent_sku` (or `parent_id`) and its `optionN_name` and `optionN_value`. The parent's options and values are made when missing, and the variant's own price, SKU and so on are set. A file may list variants before their parent. |
| Categories | Paths such as `Clothing > Tees`, separated by `|`. Missing categories are made. Variants take their parent's. Names containing `>` or `|` cannot be written as a path. |
| Barcodes | Separated by `|`. |
| Stock | The quantity wanted at the default location. The difference is recorded as an "Import" movement, so importing the same file twice moves nothing. |
| Numbers and text | Decimals use a full stop. The reader copes with Excel's semicolon separator, a byte order mark and quoted line breaks. The export writes UTF-8 with a byte order mark. |
| Formulas | Free text starting with `=`, `+`, `-` or `@` is written with a leading apostrophe, and the apostrophe is taken off on import, so a spreadsheet never runs it as a formula. |

### Checking before saving

Every import first runs all its rows inside a transaction that is rolled back, so what it reports is what would happen: `created`, `updated`, or `failed` with the reason, by spreadsheet row.

- By default, **one bad row means nothing is saved**.
- `skipInvalid=true` saves the good rows.
- `dryRun=true` only checks.

A saved row is applied on its own, so a failure that appears only then (the data changed in the meantime) leaves the rest done and is reported.

### Limits

`Piranha:Catalog:Import:MaxRows` (5000) and `MaxBytes` (10 MB). The stock column needs access to the default location, so a user limited to other locations is refused.

Not built: stock at other locations, pictures, prices for customer groups, Excel (`.xlsx`) files, and an import that runs in the background for very large files.
