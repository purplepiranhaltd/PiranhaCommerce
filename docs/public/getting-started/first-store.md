---
title: Your first store
order: 40
description: Create a product, put it on sale and place an order, using only the API.
---

# Your first store

This walkthrough uses the API only, so you can see the whole flow before you build a shop front. It assumes the sample is running on `http://localhost:5280` (see [Installation](installation.md)). The examples use `curl`; any HTTP client works.

## 1. Sign in as staff

```
curl -s http://localhost:5280/api/v1/auth/token -H "Content-Type: application/json" \
  -d '{ "email": "admin@piranhacommerce.local", "password": "Dev-Passw0rd!" }'
```

The answer holds an access token. Send it as `Authorization: Bearer <token>` on every staff call below.

## 2. Create a product

```
curl -s http://localhost:5280/api/v1/products -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{ "name": "Ruby lipstick", "sku": "LIP-RUBY", "price": 12.50 }'
```

Prices are entered without tax. Note the `id` in the answer.

## 3. Put it on sale

A product is only sold online once it is published through its online listing, and it can only be bought if there is stock:

```
curl -s -X PUT http://localhost:5280/api/v1/products/<id>/listing -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{ "published": true }'
curl -s http://localhost:5280/api/v1/products/<id>/stock/adjustments -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{ "delta": 20, "reason": "Delivery" }'
```

## 4. Buy it as a guest

No account is needed. Start a basket, add the product and check out:

```
curl -s -X POST http://localhost:5280/api/v1/storefront/guest/baskets
curl -s -X PUT http://localhost:5280/api/v1/storefront/guest/baskets/<basketId>/lines/<id> -H "Content-Type: application/json" -d '{ "quantity": 2 }'
curl -s http://localhost:5280/api/v1/storefront/guest/baskets/<basketId>/checkout -H "Content-Type: application/json" \
  -d '{ "email": "shopper@example.com", "shippingAddress": { "line1": "1 High Street", "city": "Leeds", "postalCode": "LS1 1AA", "countryCode": "GB" } }'
```

The answer is the order, with its number (the first is `ORD-1001`), a copy of every name, price and address, and the totals. The stock came off the shelf in the same step. If you added shipping methods, checkout also needs `shippingMethodId` (see [Shipping](../guides/shipping.md)).

## 5. See it in the admin

Open `/admin`, then Orders. The order is at the stage Placed. Move it through Processing and Completed, record a payment and send a parcel from the order page.

## Where next

- Put real money through it: [Payments](../guides/payments.md).
- Prices, tax and discounts: [Prices, tax and discounts](../guides/pricing.md).
- Build your own pages: the storefront API is described by the OpenAPI document at `/openapi/v1.json`.
