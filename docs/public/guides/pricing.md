---
title: Prices, tax and discounts
order: 40
description: Tax classes, sales, group and quantity prices, promo codes, discounts and how they combine.
---

## Tax classes and rules

(`/tax-classes`, `/tax-rules`): every store starts with a default class, "Standard". Add classes such as
Reduced and Zero, set a product's class on its online listing (`taxClassId`, none = the default class), and give each class a rate
per destination country (`*` for all others). So the UK can charge 20% on most goods, 5% on some and 0% on others in one basket.
Prices are entered tax-exclusive.
- Tax is worked out **per line**, by that product's class, each line rounded to the penny on its own (as an invoice adds up), then
  summed. Postage is taxed at the default class's rate. A class with no rate for the destination (and no `*` rate) is not taxed there.
- The default class cannot be deleted or switched off (make another the default instead), and a class that rules or products still
  use cannot be deleted. Rules are unique per class and country.

## Sales

(`/sales`, admin: Orders > Sales): between a start and an end (UTC), a percentage off the regular price of every product the sale covers: all
products, whole categories (with their sub-categories) or named products, and a named product can have a fixed sale price instead (`PUT /sales/{id}/products/{productId}`
with `{ "price" }`, or none to use the percentage; `PUT|DELETE /sales/{id}/categories/{categoryId}`). `enabled: false` pulls a sale early or holds it back; its status is
Off, Scheduled, Running or Ended. Everyone gets the sale price, signed in or not: `GET /storefront/guest/prices?productIds=...` (anonymous) and `/storefront/prices` answer
`{ productId, regularPrice, price, discounted, saleEndsUtc }` so a shop front can show "was / now" and a countdown. With several sales running the lowest price wins, a sale
never raises a price, and a sale and a group price do not add together: the customer pays the lower of the two, and a promo code then comes off that. Baskets, quotes and
checkout all use it (a basket line shows `regularPrice` when it is reduced), and an order keeps the price charged when the sale ends. A sale on all products changes
every price while it runs, so check the dates. Staff with write access change sales; anyone with a token can read them.

## Customer group prices

a customer group can give its members a percentage off every product (`discountPercent` on the group, 0 to 100, 2 decimal
places) and a product can carry a fixed price for a group (`PUT|DELETE /products/{id}/group-prices/{groupId}` with `{ "price" }`, `GET` lists every group
with what it pays; admin: the "Customer group prices" panel on the product page and the % field on Customer groups). What a customer pays: for each
group they belong to, the fixed price if that product has one for the group, otherwise the regular price less the group's percentage (rounded to 2
places); they pay the **lowest** of those and the regular price, so a group never makes anything dearer. Baskets, quotes and checkout all use the same
rule, so they cannot disagree; the basket line shows the `regularPrice` when a group price applies, and an order keeps the price it was charged. A
promo code then discounts the group price (10% off a 100 product at 20% group price is 8, not 10). The price follows membership: join or leave a group
and an open basket re-prices. Only identified customers get group prices: a guest basket pays the regular price. Prices are tax-exclusive, like all
prices here. To show a shopper their prices, call `GET /storefront/prices?productIds=...` (their own token) or, from a shop front acting for them,
`GET /customers/{id}/prices?productIds=...`: each answer is `{ productId, regularPrice, price, discounted }`. (The shop front's catalogue endpoints, described in [Building a shop front](storefront.md), already carry
each product's price for the shopper.)
**By category:** a group can also have a percentage for a whole category (`GET /customer-groups/{id}/category-discounts`, `PUT|DELETE
.../{categoryId}` with `{ "percent" }`; admin: Orders > Group category discounts). It covers the category's sub-categories (the nearest category that
has one set wins), replaces the group's own percentage for products in it, and `0` means no discount there. A product in several categories gets
the largest percentage. Within one group the most specific thing wins: fixed product price, then category percentage, then the group's own percentage.

## Quantity prices and order limits

(`GET|PUT /products/{id}/price-breaks`, admin: Quantity prices on the product page). A product can have up to 20 quantity breaks: from a quantity, a price each **or** a percentage
off the regular price, for everyone or for the members of one customer group (`{ "breaks": [ { "minQuantity": 5, "price": 9.00 }, { "minQuantity": 20, "percent": 25, "groupId": "..." } ] }`; a PUT replaces the list; the
quantity is 2 or more, once per group). The buyer pays the **lowest** price that applies to them: the regular price, a running sale, their group's price or a break, so a break never makes anything dearer. Baskets,
quotes and checkout all use the same calculation, so they agree, and the order keeps the unit price it was sold at. Shopper prices (`/storefront/prices`, `/storefront/guest/prices`) list the breaks worth showing
(`breaks`: quantity, unit price and the tax-inclusive unit price; a break that is no cheaper than a smaller quantity is left out). **Order limits:** the online listing has `minOrderQuantity` and `maxOrderQuantity`
(also catalogue import columns `min_order_quantity` and `max_order_quantity`): a basket line outside them is refused (400), taking a line out is always allowed, and checkout checks again (409) in case the limit
moved or baskets were merged. A variant has its own breaks and limits (it starts with its parent's limits). Not built: quantity steps, breaks in the catalogue import, and per-customer limits.

## Promo codes

(`/promotions`, `POST|DELETE /baskets/{id}/promo`): a percentage or fixed amount off the **goods**, never off postage.
Optional minimum spend, start/end dates and a limit on uses. The discount comes off before shipping and tax: it is shared across
the lines in proportion to price (to the penny, exactly), postage "free over X" is judged on the goods after the discount, and tax is
charged on what each line costs after its share. A code is checked again at checkout; if it has stopped working, checkout answers
409 instead of quietly charging full price. A limited code is used up with one conditional update, so simultaneous orders cannot
exceed the limit, and cancelling an order gives its use back (once). A code cannot be renamed once created. Every order satisfies
Total = Subtotal - DiscountTotal + ShippingTotal + TaxTotal.

## Discounts

(`/discounts`, admin: Orders > Discounts). A richer tool than promo codes. A discount is **percent off**, **an amount off**, **buy X get Y** (`buyQuantity`, `getQuantity`, and `value` percent off the ones you get, 100 for free;
the cheapest units are the ones discounted, in every group of X+Y) or **free shipping**. It applies to everything, or to named products and categories (a category includes its sub-categories), may need a minimum spend on
what it covers, a start and end, a customer group, a most-orders-overall limit and a most-orders-per-customer limit (a guest has no one to count against, so a discount with a per-customer limit is not for guests).
With no `code` it applies by itself to any basket that qualifies; with a `code` the shopper enters it the same way as a promo code (`POST /baskets/{id}/promo`), and the code cannot match a promo code. Discounts work on the
price the buyer already has (sale, group price, quantity price) and come off before shipping and tax; tax is charged on what each line costs after its share. **Stacking:** combinable discounts apply in `priority` order (then
name), each on what the earlier ones left; an `Exclusive` one is worked out alone against the full prices and used instead only when it is worth more than all the combinable ones together. The quote lists what was applied
(`discounts`) and says why a code gave nothing (`promotionProblem`, e.g. under the minimum, not for anything in the basket, used up); checkout refuses a code that gives nothing (409), spends a use of each discount that
applied with one conditional update (so simultaneous orders cannot over-use a limited discount), and keeps a copy of what was applied on the order (`discounts`). Cancelling an order gives the uses back. A promo code,
if the basket also holds one, comes off what the discounts left. Not built: a discount on the order total rather than goods, and gifts with purchase.

## Tax-inclusive display

Prices are still entered and stored without tax; a shop front can show them with tax added. The price lookups
(`GET /storefront/guest/prices`, `/storefront/prices`, `/customers/{id}/prices`) take an optional `countryCode` (or use `Piranha:Ecommerce:DefaultCountryCode`,
the shop's home country) and then also return `taxCountry`, `taxRate`, `priceIncludingTax` and `regularPriceIncludingTax`, by the rate of that product's tax class in that
country (0 when not taxed there; no tax figures when no country is known, as before). Each is the price plus its tax rounded to the penny, so a total built from unit
prices can differ by a penny from what is charged: charges are worked out per line, and the quote is the authority. A quote also returns `lines` (each with its share
of any discount, its tax, and `lineTotalIncludingTax`) and `shippingTax`, so a basket page can show tax-inclusive lines that add up to the total; order lines return
`lineTotalIncludingTax` too. Not built: entering prices tax-inclusive, and tax-inclusive figures on the basket itself (it does not know the destination: use the quote).

## Quotes

`POST /baskets/{id}/quote` returns exactly what checkout will charge (the same calculator), with a `taxes` list (name, rate, amount).
Orders keep copies of the method name and, on every line, the tax name, rate and amount, so editing settings later never changes an
order; the order's `taxes` summary is built from those copies. Orders placed before tax classes show their single flat tax row.
Total = goods + shipping + tax.
