# 0011-client-sync

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Phase 9 of [the roadmap](../vision.md#17-phased-roadmap): the client half of
synchronization. Phase 8 built a server that stores notes and can answer
"everything that changed since X" ([0010](0010-server-foundation.md)), but
nothing yet spoke to it. Three decisions were open going in and all three were
answered explicitly: how to authenticate, when to sync, and what to do about a
note changed in two places.

## Decision

### One shared token, checked in constant time

Every `/api/notes` request must carry `X-MdBolsa-Token`, compared against
`Sync:Token` on a **hash** of the presented and expected values, in constant
time. `/health` stays open, because a health probe that 401s is worse than
useless and it exposes nothing but the environment name and database host.

This is deliberately the simplest thing that closes "the API is on my network".
It is **not** per-device tokens, not rotation, not expiry, and every device that
knows the token is fully trusted. That is proportionate to a personal server on
a home network, and it is a choice to revisit rather than a permanent one -
`TokenValidator` is one class to replace with something real.

What made it safe to have *no* auth in Phase 8 was that nothing exposed the
API. That is still true: the API binds to `localhost:5080` in development, the
compose files publish Postgres on `127.0.0.1` only, and **Production refuses to
start without a token** - the same fail-fast as the connection string. An
unauthenticated note store is not a production configuration, and the server
says which auth it has on `/health` rather than implying it.

### The wire contract moves to `MdBolsa.Contracts`

Phase 8's DTOs lived in the server, and [0010](0010-server-foundation.md) flagged
that as fine "until the client has something to say". It now does, so the DTOs
and the two header names live in a dependency-free project both sides reference.
Two hand-written copies of a sync contract is exactly the drift that only shows
up in production.

### Pull, then push - and hashes, not timestamps, to decide what changed

`NoteSyncClient.SyncAsync` does the pull first, then the push. The order is the
safety property, not a style choice: a client that pushes first cannot tell a
stale local copy from a fresh one, and would overwrite a newer remote edit
without ever seeing it.

"Changed here since the server last saw it" is answered by comparing the note's
**content hash** against the hash the server last confirmed for it, which
`ISyncStateStore` keeps. Not by comparing timestamps: a clock that moved, or a
file touched by another tool, would make a note look edited. It also makes a
note that only changed over there unambiguously safe to overwrite locally.

The client skips pushing any note whose hash matches what the server confirmed,
which is what keeps a sync cheap without per-note dirty flags - and it only
records the hash *after* the server accepts the write, because recording it on a
refused write would make the next sync skip the note and lose the edit quietly.

Sync state (cursor, pushed hashes, conflicts) lives in the same SQLite file as
the other local indexes, via `SqliteSyncStateStore`. It is entirely rebuildable
and nothing in it is authoritative: the `.md` files are. The **device id** is
the exception - it is per machine, not per vault, so it lives in the app's
settings store.

### Sync happens when you ask, never while you type

The trigger is a **Sync button**, plus the vault scan that immediately precedes
and follows it. Saving a note does *not* touch the network: it would mean typing
waits on a round trip, which [vision.md §10](../vision.md#10-synchronization-philosophy)
rules out ("typing must never wait on sync").

The shell therefore does no network work on the save path at all - the Sync
button is the only caller. Debounce-after-edit, periodic sync, and
sync-on-reconnect are all still open ([vision.md §10](../vision.md#10-synchronization-philosophy)
lists them as possibilities) and none of them need a design change: they are
different callers of the same `SyncAsync`.

### Conflicts are detected and reported, never resolved

A note changed in two places is **left alone on both sides** and recorded in
`sync_conflicts`, which the status bar summarises. Phase 9 does not merge, does
not pick a winner, and does not keep a second copy of anything - that is Phase
10, and the schema already carries what it needs
([0010](0010-server-foundation.md)).

Two conflicts are counted as **one conflict**, because one note can be seen
twice in a single run (the pull finds the remote edit, then the push is refused).
"2 conflicts" for one note is a confusing thing to tell someone about their
notes.

The server grew one thing for this: `PUT` returns **409 with the stored state**
instead of 204, because a client that sent revision 4 and reads back revision 5
from another device has just learned it lost the race - invisible if the
endpoint only says "ok". A path collision (another note already lives at that
path) is translated from a raw PostgreSQL unique-violation into the same clean
409, rather than surfacing as a 500.

## Consequences

- Two notes can be *detected* as conflicting and must be looked at by a human.
  Nothing is lost, but nothing is reconciled either - a Phase 10 feature, and the
  reason the conflict list is persisted rather than just printed.
- Sync is opt-in and manual, so a second device sees nothing until someone
  presses Sync. That is the honest trade for never blocking the editor.
- 18 unit tests cover the client's decisions against a fake HTTP handler (no
  network, no database): what gets pushed, what gets written, what is refused,
  the pull-before-push order, cursor paging, a repeated cursor, a wrong token and
  an unreachable server. 6 end-to-end tests drive the real client against a real
  server and a real PostgreSQL, and return early when the dev stack isn't
  running rather than failing.
- Those end-to-end tests needed two things worth remembering: they share one
  database, so they run one at a time (`IntegrationCollection`) and delete their
  own rows afterwards; and their vault paths are namespaced per run, because the
  server allows exactly one note per path.
- The token is stored in the app's settings store, which is fine for a personal
  vault and is *not* a credential store. If this ever holds anything worth
  stealing, that changes.
