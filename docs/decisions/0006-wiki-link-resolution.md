# 0006-wiki-link-resolution

- **Status:** Accepted
- **Date:** 2026-09-17

## Context

`[[Wiki Links]]` need to resolve to an actual note so backlinks can be
computed. A link's target text can be a full relative path
(`[[Personal/Home]]`) or just a name (`[[Home]]`, `[[Welcome]]`), and the
vault may contain two notes with the same filename in different folders.

## Decision

`MdBolsa.Core.Links.LinkScanner` resolves each target in two steps:

1. **Exact relative-path match** (case-insensitive, `.md` optional) - if
   the target equals a note's relative path, use it.
2. **Unambiguous filename match** - otherwise, if exactly one note in the
   vault has that filename (regardless of folder), use it. If more than
   one note shares that filename, the link is left **unresolved** rather
   than guessing.

A target that never matches anything is also left unresolved
(`target_note_id = NULL` in the `links` table), not silently dropped -
this is what lets the app eventually show "broken" links, same as
Obsidian does, instead of pretending they don't exist.

## Consequences

- Ambiguous filenames (two notes both named `Home.md` in different
  folders) require the *full* relative path to link unambiguously - a
  bare `[[Home]]` won't resolve to either. Acceptable for a personal
  vault; revisit if this proves annoying in practice (e.g. by picking
  the alphabetically-first match instead of leaving it unresolved).
- Resolution re-runs on every `LinkScanner.Scan()`, so renaming a note
  (which `VaultScanner` already tracks as a "move" - see
  [0005](0005-stable-note-identity.md)) doesn't break links pointing at
  its *relative path* the way a naive "resolve once and cache" approach
  would: the next scan just re-resolves against the current vault
  state.
- Deleting a note doesn't need special-case cleanup of dangling
  `target_note_id` references - the next full scan naturally re-resolves
  every link from scratch, so a deleted target simply becomes
  unresolved again.
