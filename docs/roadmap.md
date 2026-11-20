# Roadmap and open design notes

Things we have agreed need doing but have not built. Each entry records the requirement as stated, so the design starts from it.

## Stock transfers between locations

Stock can be held at several locations (`StockLocation`) but cannot yet be moved between them.

Requirements:

- A transfer is **sent by a user at the sending location** and **received by a user at the receiving location**. Two people, two
  steps; neither can complete the other's step.
- Between sending and receiving the stock is **in transit**: it has left the sender's level and is not yet in the receiver's. It
  must be visible as in transit (not lost, not double counted), per product and per route.
- Every step is recorded in the stock ledger, so the ledger still explains every level. Sending writes a negative movement at the
  sender; receiving writes a positive movement at the receiver. The quantity received may differ from the quantity sent (damage,
  miscount); a difference needs to be recorded and flagged, like negative stock.
- A transfer that is never received must stay visible as outstanding.

This depends on **location-aware sign-in** (below): "sent by a user at the sending location" needs to know where a user is.

## Signing in at a location

Today there is one kind of admin user, with no location. Needed: a user can sign in *at* a particular location, so actions such as
sending or receiving a transfer, stock adjustments and (later) EPOS sales are attributed to, and restricted to, that location.

Open questions to settle when this is designed:

- Is a user assigned to one or several locations, and chooses one at sign-in when several?
- Which permissions are per location (send/receive transfers, adjust stock) and which are global (reports, catalog editing)?
- How this relates to the EPOS till: a till belongs to a location, so signing in at a till implies the location.

## Customers: personal data

Customers hold personal data. Before orders exist we need an approach for erasing or anonymising a customer who has orders (the
orders must survive for accounting). Orders will keep a snapshot of the addresses used, so anonymising a customer does not change
history.

## Not yet built (agreed direction)

- Translations for the admin UI and storefront (user-facing text is kept out of the data model so this needs no migrations).
- Customer groups (named in the vocabulary; for segmentation and pricing).
- Customer storefront login (a customer is separate from an admin user).
- Alerts and recount tasks for negative stock (the report and per-movement balances exist).
