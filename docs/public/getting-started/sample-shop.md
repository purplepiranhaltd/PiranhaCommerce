---
title: The sample shop and its data
order: 5
description: The invented cosmetics catalogue the sample host fills itself with, and how to switch it on or off.
---

The sample host (`samples/PiranhaCommerce.Web`) can fill an empty shop with a small, invented cosmetics catalogue so you have something real to browse, price and buy. The brand, products and reviews are made up for the sample. The pictures are drawn when the data is created, so there are no image files and no one's photographs.

## Switching it on

The data is created at startup when `Piranha:SampleData:Enabled` is `true`. It is on in `appsettings.Development.json` and off everywhere else, and it only ever runs when the host is in the Development environment. Running it again adds nothing twice: each category, product, zone, method, code, sale and shopper is looked up first and left alone if it is there.

To start again from nothing, drop the development database (`dotnet ef database drop` in the sample project) and run the host.

## What you get

- **Categories:** Lips (lipsticks, lip gloss, lip liners), Eyes (mascara, eyeshadow palettes), Face (foundation, blush and highlighter), Skin (cleansers, moisturisers, serums and protection) and Gift sets.
- **Sixteen products** with prices, descriptions, weights, page-title text and pictures. Lipsticks, gloss, liners, mascara, foundation, blush and highlighter come in **shades**, each a variant with its own picture and stock. The moisturiser comes in two **sizes** at different prices.
- **Stock to demonstrate with:** most shades are well stocked, a few are low (the "only 3 left" message), and some are sold out, so you can try the back-in-stock request.
- **Delivery and tax:** a United Kingdom zone and a rest-of-the-world zone, three delivery methods priced by zone (standard delivery is free over 40.00), and VAT at 20% for the UK.
- **Discounts:** the promo code `WELCOME10` (10% off orders of 20.00 or more), a 20% sale on everything in Eyes, and a quantity price on lip liners (15% off for three or more).
- **Suggestions:** related and cross-sell products for the lip, face and skin ranges.
- **Shoppers and reviews:** three sample accounts, each with the password `Demo-Passw0rd!`, and a handful of approved reviews. The addresses are `demo@example.com`, `maya@example.com` and `jess@example.com`. This password is for local development only.

## Using it in your own tests

The seeder is an ordinary class, `SampleDataSeeder`, registered in the sample host. The integration tests turn the switch off, so the shared test database stays empty, and call the seeder directly in the two tests that need a populated shop. They then use the same storefront API a shop front uses: [Building a shop front](../guides/storefront.md).
