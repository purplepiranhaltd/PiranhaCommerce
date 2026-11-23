---
title: Prices, tax and discounts
order: 40
description: Tax classes, sales, group and quantity prices, promo codes, discounts and how they combine.
---

Prices are entered and stored **without tax**. A basket works out what to charge in a fixed order, and baskets, quotes and checkout all use the same calculation, so they cannot disagree.

## How a price is worked out

1. **The unit price** is the *lowest* of: the regular price, a running sale, the customer's group price, and a quantity break. Nothing in this step can make a price dearer.
2. **Discounts** (and then a promo code) come off the goods. They work on the price the buyer already has, so they stack on top of sales and group prices. They are shared across lines in proportion to price, to the penny.
3. **Shipping** is added. Promo codes and discounts never reduce postage, except a free-shipping discount, and a "free over X" rule is judged on the goods *after* the discount.
4. **Tax** is worked out per line, by each product's tax class and the destination, on what that line costs after its share of the discount.

Every order satisfies `Total = Subtotal - DiscountTotal + ShippingTotal + TaxTotal`. The order keeps copies of everything it was charged (unit prices, the discounts applied, the shipping method name and each line's tax name, rate and amount), so changing settings later never changes an order that exists.

`POST /baskets/{id}/quote` returns exactly what checkout will charge, because it is the same calculator. It carries a `taxes` list (name, rate, amount). Show the quote's totals, not totals you add up yourself.

## Tax classes and rules

Every store starts with a default tax class, "Standard". Add classes such as Reduced and Zero (`/tax-classes`), and give each class a rate per destination country (`/tax-rules`; `*` means all other countries). Set a product's class on its online listing with `taxClassId` (none means the default class). That way the UK can charge 20% on most goods, 5% on some and 0% on others in one basket.

- Tax is worked out **per line**, by that product's class, with each line rounded to the penny on its own (as an invoice adds up), then summed.
- Postage is taxed at the default class's rate.
- A class with no rate for the destination, and no `*` rate, is not taxed there.
- The default class cannot be deleted or switched off (make another class the default instead). A class that rules or products still use cannot be deleted. A rule is unique per class and country.
- Orders placed before tax classes existed show their single flat tax row.

### Showing prices with tax

The price lookups (`GET /storefront/guest/prices`, `/storefront/prices`, `/customers/{id}/prices`) take an optional `countryCode`, or use `Piranha:Ecommerce:DefaultCountryCode` (the shop's home country). They then also return `taxCountry`, `taxRate`, `priceIncludingTax` and `regularPriceIncludingTax`, using the rate of that product's tax class in that country (0 when not taxed there). With no country known there are no tax figures.

Each tax-inclusive price is the price plus its tax rounded to the penny, so a total built from unit prices can differ by a penny from what is charged. **The quote is the authority.** A quote also returns `lines` (each with its share of any discount, its tax and `lineTotalIncludingTax`) and `shippingTax`, so a basket page can show tax-inclusive lines that add up to the total. Order lines return `lineTotalIncludingTax` too.

Not built: entering prices tax-inclusive, and tax-inclusive figures on the basket itself (it does not know the destination; use the quote).

## Showing a shopper their prices

- `GET /storefront/guest/prices?productIds=...` (no sign-in) and `GET /storefront/prices?productIds=...` (the shopper's own token) answer `{ productId, regularPrice, price, discounted, saleEndsUtc }` for each product, so a page can show "was / now" and a countdown.
- A shop front acting for a customer uses `GET /customers/{id}/prices?productIds=...`.
- The catalogue endpoints described in [Building a shop front](storefront.md) already carry each product's price for the shopper.

## Sales

A sale (`/sales`, admin: Orders > Sales) takes a percentage off the regular price between a start and an end (UTC). It can cover:

- all products,
- whole categories, including their sub-categories (`PUT|DELETE /sales/{id}/categories/{categoryId}`), or
- named products, each optionally with a **fixed sale price** instead of the percentage (`PUT /sales/{id}/products/{productId}` with `{ "price" }`, or no price to use the percentage).

`enabled: false` pulls a sale early or holds it back. A sale's status is Off, Scheduled, Running or Ended.

Everyone gets the sale price, signed in or not. With several sales running, the lowest price wins. A sale never raises a price, and a sale and a group price do not add together: the customer pays the lower of the two, and a promo code then comes off that.

A basket line shows `regularPrice` when it is reduced, and an order keeps the price charged even after the sale ends. A sale on all products changes every price while it runs, so check the dates. Staff with write access change sales; anyone with a token can read them.

## Customer group prices

A customer group can give its members a percentage off every product (`discountPercent` on the group, 0 to 100, two decimal places). A product can also carry a fixed price for a group: `PUT|DELETE /products/{id}/group-prices/{groupId}` with `{ "price" }`, and `GET` lists every group with what it pays. In the admin these are the "Customer group prices" panel on the product page and the % field on Customer groups.

**What a customer pays.** For each group they belong to: the fixed price if the product has one for that group, otherwise the regular price less the group's percentage (rounded to two places). They pay the **lowest** of those and the regular price, so a group never makes anything dearer.

- A promo code then discounts the group price: 10% off a 100 product at a 20% group price is 8, not 10.
- Prices follow membership: join or leave a group and an open basket re-prices.
- Only identified customers get group prices. A guest basket pays the regular price.
- A basket line shows `regularPrice` when a group price applies, and an order keeps the price it was charged.

### Group discounts by category

A group can also have a percentage for a whole category (`GET /customer-groups/{id}/category-discounts`, `PUT|DELETE .../{categoryId}` with `{ "percent" }`; admin: Orders > Group category discounts).

- It covers the category's sub-categories; the nearest category that has one set wins.
- It replaces the group's own percentage for products in it, and `0` means no discount there.
- A product in several categories gets the largest percentage.
- Within one group, the most specific thing wins: a fixed product price, then the category percentage, then the group's own percentage.

## Quantity prices and order limits

### Quantity prices

A product can have up to 20 quantity breaks (`GET|PUT /products/{id}/price-breaks`; admin: Quantity prices on the product page). Each break is "from this quantity, a price each **or** a percentage off the regular price", for everyone or for the members of one customer group:

```json
{ "breaks": [ { "minQuantity": 5, "price": 9.00 }, { "minQuantity": 20, "percent": 25, "groupId": "..." } ] }
```

A `PUT` replaces the list. The quantity must be 2 or more, and each quantity can appear once per group. The buyer pays the **lowest** price that applies to them (regular price, running sale, group price or a break), so a break never makes anything dearer. The order keeps the unit price it was sold at.

The shopper price lookups list the breaks worth showing as `breaks`: quantity, unit price and the tax-inclusive unit price. A break that is no cheaper than a smaller quantity is left out. A variant has its own breaks.

### Order limits

The online listing has `minOrderQuantity` and `maxOrderQuantity` (also the catalogue import columns `min_order_quantity` and `max_order_quantity`). A basket line outside them is refused (`400`). Taking a line out is always allowed. Checkout checks again (`409`) in case a limit moved or baskets were merged. A variant starts with its parent's limits and has its own after that.

Not built: quantity steps, breaks in the catalogue import, and per-customer limits.

## Promo codes

A promo code (`/promotions`; shoppers use `POST|DELETE /baskets/{id}/promo`) takes a percentage or a fixed amount off the **goods**, never off postage. It can have a minimum spend, start and end dates and a limit on uses.

- It comes off before shipping and tax, shared across lines in proportion to price (to the penny, exactly). Tax is charged on what each line costs after its share.
- "Free over X" postage is judged on the goods after the discount.
- A code is checked again at checkout. If it has stopped working, checkout answers `409` instead of quietly charging full price.
- A limited code is used up with one conditional update, so simultaneous orders cannot exceed the limit. Cancelling an order gives its use back, once.
- A code cannot be renamed once created.

## Discounts

Discounts (`/discounts`; admin: Orders > Discounts) are a richer tool than promo codes. A discount is one of:

| Kind | What it does |
|---|---|
| Percent off | A percentage off what it covers. |
| Amount off | A fixed amount off. |
| Buy X get Y | `buyQuantity`, `getQuantity` and `value` (the percent off the ones you get; 100 is free). In every group of X+Y the **cheapest** units are the ones discounted. |
| Free shipping | Removes the postage. |

**What it covers.** Everything, or named products and categories (a category includes its sub-categories).

**Conditions.** A minimum spend on what it covers, a start and end, a customer group, a most-orders-overall limit and a most-orders-per-customer limit. A guest has no one to count against, so a discount with a per-customer limit is not for guests.

**Applying it.** With no `code` it applies by itself to any basket that qualifies. With a `code`, the shopper enters it the same way as a promo code (`POST /baskets/{id}/promo`); a discount code cannot match a promo code.

**Stacking.** Discounts work on the price the buyer already has (sale, group price, quantity price) and come off before shipping and tax.

- Combinable discounts apply in `priority` order (then by name), each on what the earlier ones left.
- An `Exclusive` discount is worked out alone against the full prices, and is used instead only when it is worth more than all the combinable ones together.
- A promo code, if the basket also holds one, comes off what the discounts left.

**What the quote says.** It lists what was applied (`discounts`) and says why a code gave nothing (`promotionProblem`: under the minimum, not for anything in the basket, used up and so on).

**At checkout.** A code that gives nothing is refused (`409`). A use of each discount that applied is spent with one conditional update, so simultaneous orders cannot over-use a limited discount. The order keeps a copy of what was applied (`discounts`). Cancelling an order gives the uses back.

Not built: a discount on the order total rather than on goods, and gifts with purchase.
