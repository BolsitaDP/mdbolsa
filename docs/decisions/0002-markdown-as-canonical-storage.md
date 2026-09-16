# 0002-markdown-as-canonical-storage

- **Status:** Accepted
- **Date:** 2026-09-15

## Context

The user must retain ownership of their data — if the application
disappeared tomorrow, notes and attachments must still be accessible (see
[vision.md §4](../vision.md#4-local-data-architecture)).

## Decision

Markdown files on disk are the canonical, user-readable representation of
every note. SQLite is used only for derived state: indexes, links,
backlinks, tags, search, and synchronization metadata. SQLite is never the
only place note content exists — it can always be rebuilt by rescanning
the vault.

## Consequences

- Every feature that touches note content must read/write the `.md` file
  first; the SQLite index is a cache that can be deleted and rebuilt.
- Note identity cannot depend on the SQLite row surviving — it must be
  derivable from (or stored within) the file itself. This directly feeds
  the open "stable note identity" question tracked in
  [architecture.md](../architecture.md#open-questions-flagged-for-later-phases-not-decided-now),
  to be resolved in Phase 2.
- Corruption or loss of the local SQLite database is recoverable by
  rescanning the vault; corruption of the vault itself is not mitigated by
  SQLite and needs its own backup story (Phase 14).
