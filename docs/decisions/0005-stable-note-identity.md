# 0005-stable-note-identity

- **Status:** Accepted
- **Date:** 2026-09-16

## Context

Flagged as an open question since Phase 0
([architecture.md](../architecture.md)): path-based note identity breaks
under rename/move, which matters once multi-device sync (Phase 9)
exists. Needed to decide before designing the Phase 2 vault index schema
(see [0002](0002-markdown-as-canonical-storage.md)).

## Decision

Each note gets a stable `Guid`, stored as an `id:` field in its Markdown
YAML frontmatter. On first scan, if a note file has no `id:` field, the
vault scanner (`MdBolsa.Core.Vault.VaultScanner`) generates one and
writes it back into the file (creating a frontmatter block if none
existed). The SQLite `notes` table keys on this id, not path, so a scan
that finds an existing id at a new path is a "moved" note - reconciled
in place - rather than a delete+create.

Content hashing (SHA-256 over the full file, including frontmatter)
drives per-scan revision bumps: an unchanged hash keeps the revision, a
changed hash increments it - reserving `revision`/`content_hash`/
`updated_at` for sync, per [0002](0002-markdown-as-canonical-storage.md).

Chose a hand-rolled minimal frontmatter reader/writer
(`MdBolsa.Core.Vault.FrontMatter`) over a full YAML library (e.g.
YamlDotNet): Phase 2 only ever needs to read or inject a single `id:`
line, never to parse or rewrite the rest of the block, so it only ever
*adds* a line and never risks corrupting frontmatter fields it doesn't
understand (tags, `created`, etc.). Revisit once a phase needs to
structurally parse frontmatter (e.g. Phase 6 tags).

## Consequences

- Renames/moves are detected as moves, not delete+create - the correct
  behavior for eventual sync.
- Every note's file gets a visible `id:` line added on its first scan -
  a real, visible write to user content, not just an index-side
  operation. Deliberate trade-off: an id kept only in SQLite wouldn't
  survive the index being deleted and rebuilt from the vault, which
  [0002](0002-markdown-as-canonical-storage.md) requires must always be
  possible.
- Two files that end up sharing the same `id:` (e.g. a user manually
  copy-pasting frontmatter) are not detected in Phase 2 - the index will
  reflect only whichever one was scanned last. Not handled now since
  it isn't a mainline flow; the vault stays the source of truth
  regardless, so this is a rebuildable-index inconsistency, not data
  loss. Revisit if it proves a real problem.
