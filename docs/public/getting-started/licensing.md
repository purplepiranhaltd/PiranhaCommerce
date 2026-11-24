---
title: Licensing
order: 60
description: Free to use and change, with a small attribution notice on every page; a Commercial Licence removes the notice.
---

# Licensing

PiranhaCommerce is **source-available**. You may use it, change it and sell what you build with it, for free, as long as every page it shows people carries a small notice:

> Built on PiranhaCommerce

linked to <https://piranhacommerce.com>. If you would rather not show it, buy a **Commercial Licence**. The full terms are in the `LICENSE` file at the root of the repository and inside every package; this page explains how they work in practice, and the `LICENSE` file is what counts.

This is not an OSI "open source" licence, because of the attribution condition, and it has no copyleft: you never have to publish your own code or your changes.

## What you may do

- Use it for any purpose, including a business that sells things.
- Copy it, change it, build products and services on it, and sell them.
- Give it, or a changed version, to other people (keep the copyright notices and a copy of the licence with it).
- Keep your own changes private.

## What you must do

- **Show the notice on every web page** the software shows people: the pages of your shop front that shoppers see, and the admin pages that staff see.
- **Keep it easy to see.** It goes in the page footer (or somewhere just as easy to see), is readable (no smaller than the rest of the footer text, and in a colour that contrasts with its background), is shown to everyone without signing in or opening anything, and is not hidden or disguised.
- **You may restyle it** (colours, font, position) to suit your design. You may not change its words or its link.
- **Pages only.** API responses, emails and downloaded documents are not covered, and neither is a different application that only talks to the API.

## Showing the notice

- **The admin and the sample shop** show it for you. Leave it in place.
- **A shop front you build yourself** must add it. In a Razor page or component, use `<PiranhaCommerce.Admin.Components.Attribution />` (or add `@using PiranhaCommerce.Admin.Components` and write `<Attribution />`). Anywhere else, use the HTML in `PiranhaCommerceAttribution.Html` from `PiranhaCommerce.Core.Identity`:

```html
<a class="pc-attribution" href="https://piranhacommerce.com">Built on PiranhaCommerce</a>
```

## Removing it: the Commercial Licence

A Commercial Licence is the same licence with one difference: on the domains you have licensed, you do not have to show the notice. The terms are in `COMMERCIAL-LICENSE.md` in the repository.

- **Price:** £400 per domain, **once**. There is no renewal, and it covers later versions too. Prices are in pounds sterling and VAT is added where it applies.
- **A domain means the domain and every subdomain of it.** A licence for `example.co.uk` covers `shop.example.co.uk`, `admin.example.co.uk` and any other subdomain. A different domain, even for the same brand (`example.com` beside `example.co.uk`), needs its own licence.
- **Test and staging copies** of a licensed site are covered.
- **More than one domain** (agencies, for example) costs less per domain: domains 1 to 4 are £400 each, domains 5 to 9 are £360 each (10% off) and the 10th and after are £320 each (20% off). Each domain is priced by where it falls in your count, so 12 domains cost £4,360. The count includes domains you already hold. The pricing page has a calculator.
- **Agencies** may buy licences for client domains and transfer them to the client.
- **To buy,** send the domains and who the licence is for to the address on the pricing page and you will be invoiced; the licence starts when it is paid.

There is no key to install and nothing is checked at run time. Once a licence is in force, turn the notice off in your own host's settings (this switches off the notice in the admin and the sample shop; remove it from your own shop front yourself):

```json
{ "PiranhaCommerce": { "Branding": { "ShowAttribution": false } } }
```

Without a Commercial Licence in force, removing or hiding the notice breaks the licence. Only turn it off on domains you have licensed: on any other domain the notice must still show.

## If the notice is missing

The licence ends for you if you break the attribution condition and do not put it right within 30 days of being told. Putting it right restores it. People you gave the software to keep their own licence from Purple Piranha.

## Your own packages and plugins

A plugin you write for PiranhaCommerce is your own work and you may licence it as you choose. The attribution condition applies to the pages the software shows, not to your plugin's source. Anything you copy out of PiranhaCommerce into it stays under this licence.

## Third-party components

PiranhaCommerce uses components made by others (for example QRCoder and SkiaSharp, both MIT licensed). They stay under their own licences.
