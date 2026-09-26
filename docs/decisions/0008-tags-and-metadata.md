# 0008-tags-and-metadata

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Phase 6 of [the roadmap](../vision.md#17-phased-roadmap): tags
(`vision.md` §1) and note metadata. The vault's canonical format is plain
Markdown with YAML frontmatter, so both features have to be *read* out of files
the user owns and may have written by hand, in any editor. Nothing may be
rewritten or "normalized" in a way that could lose a field the app doesn't
understand.

## Decision

**Tags come from two places, and are stored normalized.**

- Frontmatter `tags:` — flow sequence (`tags: [a, b]`), bare/comma-separated
  scalars (`tags: a, b`), or block sequence (`- a` under an empty `tags:`).
  Only the first top-level `tags:` key counts, matching YAML's own behaviour.
- Inline `#hashtags` in the body, as Obsidian supports. A `#` is a tag only
  when it starts a word (`\A` or preceded by whitespace/`(`/`[`/`/`/`"`/`'`) and
  is immediately followed by tag characters — which is what keeps ATX headings
  (`# Title`, a space after the `#`), `C#`/`F#`, `foo#bar`, and URL fragments
  (`...#anchor`) out. Fenced code blocks and inline code spans are skipped
  wholesale; a tag must contain at least one letter, so `#42` stays an issue
  reference rather than becoming a tag.

Both paths run through one `TagParser.Normalize`: trimmed, leading `#`
stripped, lowercased, trailing `/` dropped. So `development` in frontmatter and
`#Development` in the body are one tag, not two, and `project/` is `project`.
`/` is otherwise kept, so `homelab/server` stays nestable for a later phase.

Deliberately **not** done: tag renaming/merging across the vault, tag
suggestions/completion, and YAML-validating the frontmatter. A frontmatter
value is only accepted as a tag if the whole value looks like one
(`^[\p{L}\p{N}_/-]+$`) — a free-text value like `tags: ["a note about docker"]`
is ignored rather than half-interpreted. Parsing is regex-based, like
[`WikiLinkParser`](../../src/Client/MdBolsa.Core/Links/WikiLinkParser.cs): a tag
is a `#` plus tag characters, and the only Markdown structure that matters is
knowing what *isn't* a tag. Markdig stays deferred (see
[0007](0007-search-with-fts5.md) for the same call on the search side).

**Storage: one `(note_id, tag)` table, no separate tag table.**

`note_tags (note_id, tag)` with `PRIMARY KEY (note_id, tag)` and an index on
`tag`; per-tag note counts come from `GROUP BY tag`. A standalone `tags` table
would need its own orphan cleanup on every scan, and the only thing it would buy
is a precomputed count — which is a `GROUP BY` away. Tags therefore cannot
outlive the last note that used them, by construction rather than by
convention.

`TagScanner` reindexes every note's tags on every scan and replaces each note's
rows wholesale, the same full-reindex-not-incremental approach (and same
reasoning) as `LinkScanner` and `SearchScanner` — see
[0006](0006-wiki-link-resolution.md) and [0007](0007-search-with-fts5.md).

**Metadata is displayed, never interpreted.**

`FrontMatter.ReadFields` returns top-level `key: value` pairs for the UI to
show: unindented scalar lines only, values verbatim (quotes stripped), block
sequences and nested maps skipped. A key whose value lives on the lines below it
comes back with an empty value rather than a guess. Combined with the indexed
`NoteMetadata` (path, revision, timestamps) and the note's tags, that's the
whole of "note metadata" for this phase: a read-only view of what the note
declares. Writing metadata back (e.g. an `aliases:` field the app maintains)
would be the first case of the app authoring YAML, and is not needed yet — so
`FrontMatter` still has exactly one write path, injecting `id:`.

## Consequences

- Tags are a derived index, exactly like links and search: deleting
  `index.db` and rescanning rebuilds them, and the `.md` files remain the only
  copy of the data (see [0002](0002-markdown-as-canonical-storage.md)).
- The tag panel and search box are mutually exclusive views of the notes list
  in the WinUI shell — searching clears an active tag filter and says so —
  rather than a combined "tag AND query" search. Combining them is a natural
  Phase 7+ addition once the graph view has a real filter model to hang off.
- Tag filtering is click-driven, never `TextChanged`-driven, for the reason
  documented in `MainPage`'s class comment (the Phase 3 `TextBox.Text` getter
  crash).
- Tags are not currently searchable through FTS5: the search index covers
  title + body only. Since frontmatter is excluded from the body by
  `FrontMatter.Body`, searching a tag finds notes that mention it inline but not
  notes that only declare it. Worth revisiting if it turns out to matter.
- Case is lost by design (everything is stored lowercase), so a vault that
  deliberately distinguishes `#Work` from `#work` cannot be represented. Acceptable
  for a personal vault; the fix would be a case-sensitive index, not a parser
  change.
