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

A Commercial Licence is a separate written agreement with Purple Piranha that lets you remove the notice. There is no key to install and nothing is checked at run time: once you hold a licence that is in force, turn the notice off in your own host's settings (this switches off the notice in the admin and the sample shop; remove it from your own shop front yourself):

```json
{ "PiranhaCommerce": { "Branding": { "ShowAttribution": false } } }
```

Without a Commercial Licence in force, removing or hiding the notice breaks the licence. To ask about one, email the address on <https://piranhacommerce.com>.

## If the notice is missing

The licence ends for you if you break the attribution condition and do not put it right within 30 days of being told. Putting it right restores it. People you gave the software to keep their own licence from Purple Piranha.

## Your own packages and plugins

A plugin you write for PiranhaCommerce is your own work and you may licence it as you choose. The attribution condition applies to the pages the software shows, not to your plugin's source. Anything you copy out of PiranhaCommerce into it stays under this licence.

## Third-party components

PiranhaCommerce uses components made by others (for example QRCoder and SkiaSharp, both MIT licensed). They stay under their own licences.
