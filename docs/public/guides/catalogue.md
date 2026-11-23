---
title: Catalogue and stock
order: 10
description: Products, categories, barcodes, pictures, variants, stock, locations, transfers and import/export.
---

This page covers everything about what you sell and where it is held. Everything here is in the shared `PiranhaCommerce.Catalog` package, used by every sales channel.


## Categories

a tree (`ParentId`), with products in any number of categories. Cycles are rejected; a category with
sub-categories cannot be deleted (409). `PUT /api/v1/products/{id}/categories` replaces the whole set.

## Barcodes

several per product. GTIN-8/12/13/14 codes are check-digit validated and stored left-padded to 14 digits, so a
UPC-A and its EAN-13 form are the same barcode and cannot belong to two products. Look a product up with
`GET /api/v1/products/lookup?barcode=...` (a query string, because Code 128 values can contain `/`).

## Stock

a quantity per product (`decimal(18,4)`, so weighed goods work) changed only by adjustments that are recorded in an
append-only ledger (`StockMovement`). Adjustments are atomic and safe under concurrency and transient-failure retries. By
default stock cannot go below zero; callers that must record physical reality (EPOS) pass `AllowNegative`.
A product with stock history cannot be deleted (409), because the ledger is an audit trail.

## Stock locations

stock is held per location (a warehouse, a shop). A default location always exists and is used when a
caller names none, so single-site shops never need to think about it. Locations are deactivated, not deleted. Each movement
records the balance at its location afterwards.

## Negative stock report

`GET /api/v1/stock/negative` (and Catalog > Negative stock in the admin) lists every product and
location below zero, for channels such as EPOS that must record a sale even when the system thought it had none.
A product drops off the list once a recount brings it back to zero or more.

All three have admin panels on the product edit page and API endpoints; API errors map to 400 (validation) and 409 (conflict).

## Product pictures

(`/products/{id}/images`, admin: Pictures on a product page): needs a storage package (`IFileStore`; the sample host uses local disk). Upload is
`multipart/form-data` with `file` and an optional `altText`; the type is decided from the file's own bytes (JPEG, PNG, WebP or GIF; SVG is refused because it can carry script),
limited by `Piranha:Images:MaxBytes` (default 10 MB) and `MaxPerProduct` (20). With an imaging package, the picture is read (a file that cannot be read is refused), turned upright from its
camera orientation data, and smaller copies are made at upload (`Piranha:Images:Sizes`, default `160,480,1024` longest side; never enlarged). The first picture is the main one:
`PUT .../images/order` sets the order, `PUT .../images/{id}` the alt text, `DELETE` removes the picture and its files (deleting a product removes all of its files). A variant with no
pictures of its own returns its parent's, marked `inherited`. Shop fronts and browsers fetch a picture, with no sign-in, at the `url` in its description:
`GET /api/v1/storefront/guest/product-images/{id}?width=300` gives the smallest made size at least that wide (or the original), with `ETag` and a day of caching. A picture is found
by its own unguessable id, so it stays reachable when its product is unpublished: treat the address as the secret. Not built: cropping, watermarks, video, a bulk upload, and
resizing for pictures uploaded before an imaging package was added.

## Slugs and page text

(products and categories): `slug` (lower case letters, digits and single hyphens; unique; lower-cased for you), `metaTitle` and `metaDescription`, and
`GET /products/by-slug/{slug}` and `GET /categories/by-slug/{slug}` for a shop front that routes by address. A PUT replaces them, so leaving one out clears it. Not built: a slug
history with redirects when one changes, and translated slugs.

## Options and variants

(`/products/{id}/options`, `/products/{id}/variants`, admin: Options and variants on a product page): a product that comes in options
(Size, Colour) gets those options with their values, then a variant for each combination (`POST .../variants/generate`, or one at a time with `POST .../variants`
and one value per option). A variant is an **ordinary product**: its own SKU, price, stock, barcodes, listing, and its name is the parent's plus its values
("Tee - M / Blue"). So stock, baskets, orders, sales and group prices need no special cases. The product with the options is the **parent**: it holds the shared name, description
and categories and **cannot be bought itself** (adding it to a basket is refused: add a variant). Rules: at most 3 options, 50 values each and 200 variants; a value or
option in use, a parent with variants, and options on a variant are refused (409); a new option cannot be added once variants exist. New variants copy the parent's
online listing (published, tax class, weight) and its description, but not its categories: a category sale or group category discount reaches variants through the parent's
categories. A sale or group price naming the parent product does not cover its variants (name the variants, or use a category). `GET /products/{id}/variants` returns
a parent's options and variants, or for a variant its parent and values. A variant has its own pictures. Not built: price as an adjustment to the parent's price, renaming values,
variants updating their name when the parent is renamed, and a storefront endpoint (a shop front reads these with its own API token).

## Catalogue import and export

(CSV; `GET /products/export?search=`, `POST /products/import` with a `file`, admin: Catalog > Import and export). One row per product, with the columns
`id, sku, parent_id, parent_sku, name, description, price, slug, meta_title, meta_description, categories, barcodes, stock, option1_name, option1_value` (and 2, 3), plus what other packages add:
the online store adds `published, tax_class, weight_grams` (a package adds columns with `IProductImportExtension`). **Start from an export.** A row is matched by `id`, else `sku`: it is updated if it
exists, otherwise created (a new product needs a name and a price). A **blank cell leaves the field alone** and **`[clear]` empties it**; a column that is not in the file is not touched; an unknown
column refuses the file. **Variants** are rows with `parent_sku` (or `parent_id`) and their `optionN_name` and `optionN_value`: the parent's options and values are made when missing, and the
variant's own price, SKU and so on are set; a file may list variants before their parent. `categories` are paths (`Clothing > Tees`) separated by `|`, and missing categories are made; variants
take their parent's. `barcodes` are separated by `|`. `stock` is the quantity wanted at the default location: the difference is recorded as an "Import" movement, so importing the same file again
moves nothing. Decimals use a full stop; the reader copes with Excel's semicolon separator, a byte order mark and quoted line breaks; the export writes UTF-8 with a byte order mark and guards
free text that starts with `=`, `+`, `-` or `@` (written with a leading apostrophe, taken off on import) so a spreadsheet never runs it as a formula. **Checking before saving:** every import first
runs all its rows in a transaction that is rolled back, so what it reports (`created`, `updated`, or `failed` with the reason, by spreadsheet row) is what would happen. By default one bad row means
**nothing is saved**; `skipInvalid=true` saves the good rows, and `dryRun=true` only checks. A saved row is applied on its own, so a failure that appears only then (the data changed meanwhile) leaves the rest
done and is reported. Limits: `Piranha:Catalog:Import:MaxRows` (5000) and `MaxBytes` (10 MB). The stock column needs access to the default location (a user limited to other locations is refused). Not built:
stock at other locations, pictures, prices for customer groups, Excel (.xlsx) files, and an import that runs in the background for very large files. Names with `>` or `|` in a category cannot be written as a path.

## Stock transfers

(`/api/v1/stock/transfers`, admin: Catalog > Stock transfers): move stock between locations in two steps by two people.
`POST` sends it: the stock leaves the sending location at once (refused if there is not enough) and the transfer is **in transit**, on neither shelf but
listed at `GET /stock/in-transit` (per product and route) and as `inTransit` on a product's stock level. `POST /{id}/receive` (by a different user, unless
`Piranha:Stock:AllowSameUserToReceive` is true) adds what arrived to the receiving location; send `{ "lines": [{ "productId", "quantity" }] }` to say
less (or more) arrived than was sent, and the transfer is flagged (`hasDiscrepancy`, filter `?discrepancies=true`). `POST /{id}/cancel` while in
transit puts the stock back. Every step is a ledger movement ("Transfer sent", "Transfer received", "Transfer cancelled") carrying the `TRF-` reference
and the user. A transfer is received or cancelled once however many people try at the same moment. The menu shows how many are in transit.

## Staff locations

(`GET /staff/locations`, `PUT /staff/{userId}/locations` with `{ "locationIds": [...] }`, admin: Catalog > Staff locations): limit a
staff user to certain stock locations. They can then only send, receive, cancel and manually adjust stock at those (others get `403`). A user with none
ticked is not limited. The location is named on each action, so there is no choose-a-location step at sign-in.
