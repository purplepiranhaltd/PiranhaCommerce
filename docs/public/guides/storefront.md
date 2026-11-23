---
title: Building a shop front
order: 5
description: The storefront API a shop front uses to browse the catalogue, show prices and suggest products.
---

A shop front is the part shoppers see. You build it with whatever you like; Piranha Commerce gives it an API under `/api/v1/storefront`, and the same services are available in-process if your shop front runs in the same application (the sample shop does this).

Everything in this page is open to anyone, signed in or not. A shopper who sends their token also gets **their own prices** (customer groups). Anything else a shopper does (baskets, checkout, account, wishlist, reviews) is described in the other guides: [baskets and orders](baskets-orders.md), [customers and shopper accounts](shoppers.md) and [reviews, wishlists and suggestions](engagement.md).

## What a shopper can see

Only categories that are switched on and products that are **published** appear. A product is published through its online listing (`PUT /products/{id}/listing`). A switched-off category hides everything under it. Variants are never listed on their own: a product that comes in options appears once, and its variants are inside it.

## Categories

`GET /storefront/categories` returns the category tree. Each category has its `name`, `slug`, `description`, page-title text, the number of products on sale in it and below it (`productCount`) and its `children`.

## Products

`GET /storefront/products` lists products. All the query values are optional:

| Query | Meaning |
|---|---|
| `category` or `categoryId` | A category by slug or id. Its sub-categories are included. |
| `q` | Words to find in the name, SKU or description. Every word must appear, in any letter case. |
| `sort` | `Name` (default), `PriceLowToHigh`, `PriceHighToLow` or `Newest`. Price sorting uses the regular price. |
| `minPrice`, `maxPrice` | Limits on the regular price. |
| `inStock` | `true` for only products that can be bought now. |
| `page`, `pageSize` | Paging. The answer has `items`, `page`, `pageSize`, `totalCount`. |
| `countryCode` | The destination, for tax-inclusive prices. The shop's home country is used when empty. |

Each item has the product's `name`, `sku`, `slug`, a short `summary` (the first line of the description), its `price`, whether it `hasVariants`, whether it can be bought (`available`), `left` (how many, only when there are 5 or fewer of a simple product), its main `image` and its `reviewCount` and `reviewAverage`.

For a product that comes in options, `price` is the **cheapest published variant's** price, so a page can say "from £9.00". `available` is true when any variant can be bought.

### One product

`GET /storefront/products/{id}` or `GET /storefront/products/by-slug/{slug}` returns the whole page: description, page-title and meta text, price, all pictures in order, the categories it is in, order limits (`minOrderQuantity`, `maxOrderQuantity`), the review totals and, for a product with options, `options` (each with its values in order) and `variants`.

Each variant has its own `productId` (this is what you add to a basket), `name`, `sku`, the chosen `values`, `price`, `available`, `left` and its own `images` (empty means use the product's). Give the id of a variant and you get its parent, with `selectedVariantId` set, so a link to one shade opens the right page with that shade chosen.

A product that does not exist, or is not published, is a `404`.

## Prices

Prices come from the same calculation baskets and checkout use, so the page and the basket agree. Each `price` is:

| Field | Meaning |
|---|---|
| `price` | What this shopper pays for one: the lowest of the regular price, a running sale, their group's price and so on. |
| `regularPrice` | The usual price. Show it struck through when `discounted` is true. |
| `saleEndsUtc` | When a sale price ends, for a countdown. |
| `breaks` | Cheaper prices for buying more (quantity prices), worth showing. |
| `priceIncludingTax`, `regularPriceIncludingTax`, `taxRate`, `taxCountry` | Tax-inclusive figures for the destination. |

Prices are stored without tax. A basket adds tax per line, so a total built from unit prices can differ from what is charged by a penny: the basket quote is what is charged. See [Prices, tax and discounts](pricing.md).

## Suggestions

`GET /storefront/products/{id}/suggestions` returns the related and cross-sell products staff chose for a product, and `GET /storefront/cross-sell?productIds=...` returns cross-sell suggestions for a whole basket. Both show only products on sale, priced for the shopper. See [Reviews, wishlists and suggestions](engagement.md).

## Pictures

Each picture has a `url`. Fetch it with no sign-in, and add `?width=300` to get the smallest made size at least that wide. Pictures are cached for a day. See [Catalogue and stock](catalogue.md).
