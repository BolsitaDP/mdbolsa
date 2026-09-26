# Synchronization

The server foundation exists (Phase 8, [0010](decisions/0010-server-foundation.md));
**no client sync does** - that's Phase 9. This document records the contract
the server already implements, so the client half can be built against it, and
what's deliberately still open.

## Philosophy (unchanged, and still binding)

- **Local-first**: writing a note never depends on the server or the network.
  Sync is a background concern layered on top of a fully-functional offline app.
- **Incremental**: never re-upload the whole vault. The server answers "give me
  everything changed since X" with `GET /api/notes/changes`.
- **Asynchronous**: typing must never wait on sync. Trigger choice is still
  deferred to Phase 9; the debounce/periodic/manual options in
  [vision.md §10](vision.md#10-synchronization-philosophy) are all still open.

## What the server already stores (the sync contract)

One row per note, carrying the identity and revision metadata §10 asks for:

| Field | Meaning |
|---|---|
| `id` | The note's frontmatter `id:` — stable across renames and moves ([0005](decisions/0005-stable-note-identity.md)) |
| `relative_path`, `title`, `content` | The note, stored byte-for-byte. The server never parses Markdown |
| `content_hash` | Lets a client skip content it already has |
| `revision` | Per-note and monotonic — what makes a conflict detectable |
| `device_id` | Which device wrote last |
| `updated_at` | The sync watermark |
| `deleted_at` | Tombstone. A deletion is a row that survives, not a missing row |

Endpoints: `GET/PUT/DELETE /api/notes/{id}` and `GET /api/notes/changes`.

Two rules the client must know:

- **Paging is a `(updated_at, id)` watermark.** Both halves matter: notes can
  share a timestamp, and a timestamp-only cursor skips rows on the boundary.
  Feed `nextCursor` back as `cursorAt` + `cursorId`.
- **A stale write is not applied.** Writes are gated on `updated_at >= the
  stored value`, so last writer by clock wins and an older write is dropped.
  This is *not* conflict resolution, and the server does not report the drop —
  see below.

## Open for Phase 9 (client half)

- How the client discovers a change: polling `/changes` on a debounce, on app
  startup/shutdown, on network reconnect, or a manual Sync action.
- Where the client stores its own sync cursor (a local settings file, next to
  the SQLite index it already keeps).
- **Authentication.** The API has none, deliberately and temporarily
  ([0010](decisions/0010-server-foundation.md)). A bearer token per device is the
  simplest thing that fits a personal self-hosted server. This is the first thing
  to add before the server is reachable from anywhere but localhost.
- Integration tests against the compose database, replacing the hand-run `curl`
  sequence used to verify Phase 8.

## Conflicts

Unchanged from [vision.md §11](vision.md#11-conflict-handling) and still
Phase 10. What Phase 8 guarantees is only that a conflict is *detectable*: both
revisions survive, `device_id` says who wrote last, and the stale write is not
silently applied over the newer one. Nothing merges, nothing is reported back to
the losing device, and no version history is kept yet. The delete endpoint
stamps tombstones with the server's clock rather than the client's, which is
fine for one device and will need to change when the second one arrives.
