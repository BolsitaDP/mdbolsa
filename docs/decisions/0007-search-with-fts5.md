# 0007-search-with-fts5

- **Status:** Accepted
- **Date:** 2026-09-17

## Context

Global search (`vision.md` §1) needs full-text search over note titles and
bodies. Options considered: a hand-rolled inverted index, an embedded
library like Lucene.NET, or SQLite's built-in FTS5 extension.

## Decision

Use SQLite's **FTS5** virtual table (`notes_fts`), via the same
`Microsoft.Data.Sqlite` connection already used for everything else -
no new NuGet dependency. Verified empirically (not assumed) that the
bundled native SQLite build includes FTS5 by writing
`SqliteSearchIndexTests` first and running them before building
anything else on top: all pass, including `snippet()` and `bm25()`
ranking.

`MdBolsa.Core.Search.SqliteSearchIndex`'s query builder never exposes
raw FTS5 query syntax to the user - a plain search box shouldn't let a
stray `"`, `-`, or `AND` throw a syntax error. Each whitespace-separated
term in the user's query becomes a quoted phrase
(`"term1" "term2"`), which SQLite implicitly ANDs together. This means
no prefix/wildcard matching yet (searching `dock` won't find "Docker") -
acceptable for a first pass; revisit with explicit `term*` prefix
syntax if it proves too limited in practice.

`SearchScanner` reindexes every note's title + body (via
[`FrontMatter.Body`](../src/Client/MdBolsa.Core/Vault/FrontMatter.cs),
which strips the leading frontmatter block so ids/tags don't show up in
search matches) on every scan - same full-reindex-not-incremental
approach as `LinkScanner`, for the same reason: simple, and fast enough
at personal-vault scale.

## Consequences

- This is squarely "can the platform already provide this" from
  [vision.md §14](../vision.md#14-development-philosophy): SQLite (which
  the app already embeds for every other index) ships a mature
  full-text search engine, so there was no real case for Lucene.NET or
  a hand-rolled inverted index here.
- Search quality is whatever FTS5's default `unicode61` tokenizer + BM25
  ranking give - no custom stemming, synonyms, or typo tolerance.
  Sufficient for a personal vault; revisit only if it proves
  inadequate in practice.
- Reindexing is tied to the same "Open vault" / Save actions as the
  vault and link scanners, so search results can lag by one scan cycle
  behind an unsaved edit - consistent with how backlinks already behave
  (see [0006](0006-wiki-link-resolution.md)).
