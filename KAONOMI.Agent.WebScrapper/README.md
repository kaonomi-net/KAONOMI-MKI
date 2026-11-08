[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.Agent.WebScrapper

Background host of the **web scraper**: the M-A-R-I agents (collect → analysis → actions) visit the web pages of the
active **collection plans**, keep a screenshot and the pictures of each page, cut its text into paragraphs and extract
the entities of each paragraph with the NER of the Kaonomi server.

## Run

```bash
cd KAONOMI.Agent.WebScrapper
dotnet run
```

A run every `scrapeIntervalMinutes` (60 by default); Ctrl+C stops the loop.

| Argument | Effect |
| --- | --- |
| `--once` | One run, then the host stops |
| `--url <url>` | Visits this URL instead of the collection plans (repeatable) |
| `--mode fixed\|explore\|random` | Mode of the `--url` URLs (fixed by default) |
| `--keywords "a,b"` | Keywords of the `--url` URLs |
| `--plan <id>` | Visits this collection plan now, active or not, pages visited recently included (the **LAUNCH AGENT** button of the collection plan window runs `--once --plan <id>`) |

```bash
dotnet run -- --once --url "https://fr.wikipedia.org/wiki/Commission_europ%C3%A9enne"
```

Needs: the search engine (`http://localhost:9200`), MongoDB for the cluster (`mongodb://localhost:27017`), the Kaonomi
server for the NER (`dialogServerUrl`, `http://localhost:49166`), and Microsoft Edge (or Chrome with
`"browserChannel": "chrome"`; with an empty channel, the browser of Playwright, installed with
`pwsh bin/Debug/net8.0/playwright.ps1 install chromium`).

## What it does

| Agent | Role |
| --- | --- |
| Collect | The URLs of the websites of the active collection plans (`isActive`, not deleted), except those visited less than `revisitHours` ago; or the `--url` URLs |
| Analysis | Visits each URL with a headless browser: `fixed` the page only, `explore` the links of the same site up to `maxDepth` / `maxPagesPerUrl` (links holding a keyword first), `random` the page and `randomPages` of its links. Respects robots.txt. Stops at `runBudgetMinutes` (the rest goes to the next run) |
| Actions | Saves the pages (index `webpage`) and their paragraphs with their entities (index `webparagraph`) |

For each page:

- **Screenshot** (full page, PNG): `Pictures/kaonomi/webscrapper/<yyyy-MM>/<page id>.png`, the id of the `webpage` document.
- **Pictures** of the page (lazy pictures and og:image included, at most `maxImagesPerPage`, those under `minImageBytes`
  left out): `Pictures/kaonomi/webscrapper/<yyyy-MM>/<yyyy-MM-dd_HH-mm-ss-fff>/`, named `001-<name>.<ext>`; the folder
  and each file are kept in `webpage.images`.
- **Paragraphs**: the text blocks (p, li, blockquote, headings, dd, figcaption, pre) outside the menus, headers,
  footers, forms and scripts, at least `minParagraphLength` characters; each one a `webparagraph` document
  (`<page id>-<rank>`) with the entities of `POST /ner` and the keywords found.

## Configuration

`config.json` next to the executable: the usual Kaonomi settings and `scrapeIntervalMinutes`, `runBudgetMinutes`,
`revisitHours`, `maxPagesPerUrl`, `maxDepth`, `randomPages`, `maxImagesPerPage`, `minImageBytes`,
`minParagraphLength`, `pageTimeoutSeconds`, `delayBetweenPagesMs`, `respectRobotsTxt`, `browserChannel`, `userAgent`,
`outputFolder` (empty: `Pictures/kaonomi/webscrapper`).

Silo ports: 11118 / gateway 30007.

## Dependencies

- `KAONOMI.Core` (models `WebPage`, `WebParagraph`, `CollectionPlan`), M-A-R-I / Orleans hosting
- Microsoft.Playwright, HtmlAgilityPack

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
