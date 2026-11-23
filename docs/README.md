# docs

- `public/` is the published documentation (Markdown with a small front matter block: `title`, `order`, `description`; folders are sections named in `public/sections.json`). Link between pages with relative `.md` paths. The site in `site/PiranhaCommerce.Site` serves it, and its tests check every page and link.
- `roadmap.md` and `vocabulary.md` are internal working notes and are never published.
