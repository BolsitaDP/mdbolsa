# 0010-server-foundation

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Phase 8 of [the roadmap](../vision.md#17-phased-roadmap): the Raspberry Pi
server foundation ([vision.md §6](../vision.md#6-raspberry-pi-server)). Two
decisions were open going in, and both were answered explicitly rather than
drifted into: **PostgreSQL from day one** (not SQLite-while-developing), and
**no authentication for now**.

## Decision

### PostgreSQL from the start, via Npgsql, no ORM

`MdBolsa.Server` is an ASP.NET Core minimal API over one `notes` table, with
raw `Npgsql` - the same "the database is storage, not a rich domain model" call
the client made with `Microsoft.Data.Sqlite` and no EF Core
([architecture.md](../architecture.md#dependencies-phase-0near-term)).

SQLite-until-deploy was rejected because it would have meant two storage
dialects, two sets of query semantics, and a migration of real data at exactly
the moment the server first holds something irreplaceable. The cost of the
choice is Docker in the development loop; that cost is paid once, in
`docker compose up -d`, and it's the same Postgres the Pi runs, so "works on my
machine" and "works on the Pi" stay the same statement.

### The schema is the sync contract, so it's shaped for Phase 9/10 now

One table, created idempotently at boot (`CREATE TABLE IF NOT EXISTS` + a
`schema_version` row) - no migration framework, the same reasoning as the
client's SQLite indexes: a framework earns its place when a phase needs to
*alter* a table, and Phase 10 is that phase.

The columns are exactly what [vision.md §10](../vision.md#10-synchronization-philosophy)
asks for, plus two additions that the brief only implies:

| Column | Why it exists |
|---|---|
| `id` | Note identity, from the client's frontmatter `id:` ([0005](0005-stable-note-identity.md)) |
| `relative_path`, `title`, `content` | The note itself, stored byte-for-byte; the server never parses Markdown |
| `content_hash` | Lets a client skip re-downloading content it already has |
| `revision` | Per-note, monotonic: what makes a conflict *detectable* in Phase 10 |
| `device_id` | Who wrote last - needed by conflict resolution and any audit trail |
| `updated_at` | The sync watermark |
| `deleted_at` | **A tombstone, not a `DELETE`** - see below |

Two decisions worth spelling out:

- **Deletes are tombstones.** A hard-deleted row is indistinguishable from a
  note that was never synced, so a client that was offline would never learn the
  note is gone. The partial unique index (`WHERE deleted_at IS NULL`) keeps
  tombstones from blocking re-creating a note at the same path.
- **The server never merges, and a stale write never clobbers a newer one.**
  Every write is gated on `EXCLUDED.updated_at >= notes.updated_at`. Last writer
  by clock wins; an older write is silently not applied. That is *not* conflict
  resolution - [vision.md §11](../vision.md#11-conflict-handling) is Phase 10 -
  but it means a device with a wrong clock can't destroy newer work, and the
  data needed to detect the conflict is already there.

### Paging is a `(updated_at, id)` watermark, not a timestamp

`GET /api/notes/changes` returns rows ordered by `(updated_at, id)` and hands
back the last row as `nextCursor`. Both halves are needed: several notes can
share a timestamp, and a timestamp-only cursor silently skips rows on the
boundary. The cursor pair is always passed as *typed* parameters behind a
`@hasCursor` flag, because a NULL parameter would make the row comparison
`unknown` and a paging client would get the first page forever.

### No authentication - explicitly, and only because it's not reachable

There is no auth in this phase, on purpose, and the server says so out loud:
`/health` reports `"authentication": "none"` and the startup banner logs
`auth=none`. What makes that safe *today* is that nothing exposes it: the API
binds to `localhost:5080` in development and the compose file publishes
Postgres on `127.0.0.1` only.

**This is the first thing to change before the Pi.** What it needs is not
decided here: a bearer token per device is the simplest thing that fits a
personal self-hosted server, and §9 already anticipates "authentication
secrets" in production configuration. Deciding it in Phase 9, when there's a
real client to authenticate, is better than guessing now.

### Dev/Prod isolation is enforced in code, not by discipline

[vision.md §7](../vision.md#7-development-vs-production) calls this
"extremely important", so it isn't left to configuration discipline:

- In **Production the server refuses to start** without an explicit
  `ConnectionStrings:Postgres`. There is no default to fall back to, so a
  development build cannot reach production by accident - pointing it at
  production requires supplying a value that appears nowhere in this repo.
- In **Development** it falls back to a local throwaway database, so the daily
  loop stays one command.
- The environment and the database host (never the password) are logged at
  startup, so a misconfigured deploy is obvious in the first lines of output.
- `dotnet run` without `launchSettings.json` defaults to **Production**, so the
  server ships a `launchSettings.json` with a Development profile. This was
  found the honest way: the first run refused to start.

All persistent state lives under one configurable root, `APP_DATA_PATH`
(`docker-compose.yml`), so moving the Pi from microSD to a SATA SSD is a data
copy plus a changed environment variable - no code change, no rebuild, which is
what §6 requires.

### The server owns no note semantics

It stores what the client sends and never parses Markdown, computes hashes, or
resolves links. Those stay in the client ([0002](0002-markdown-as-canonical-storage.md)),
and the `.md` file remains the source of truth. The sync DTOs are the server's
own wire contract and the server does **not** reference `MdBolsa.Core`; when the
client starts speaking to the server in Phase 9, that contract needs a home
both sides can reference.

## Consequences

- Development now needs Docker running (`docker compose up -d`). In exchange,
  development and production share one database engine.
- `NoteStore` has no unit tests, because testing it needs a real PostgreSQL.
  What's tested is `NoteValidation` (14 tests), the part with the decisions in
  it. Integration tests against the compose database are a Phase 9 item, and
  should replace the hand-run curl sequence used to verify this phase.
- **Known gap, deliberately left:** the delete endpoint stamps the tombstone
  with the *server's* clock, while upserts take the client's `updatedAt`. With
  one device that's fine; with two, a delete from a device whose clock lags
  loses to the note's own `updated_at` and silently does nothing. Phase 9 should
  make the delete timestamp client-supplied like every other write.
- Attachments (filesystem storage) and backups are Phase 11 and Phase 14; the
  compose file is where the backup service will eventually sit.
- Two things about Npgsql cost time and are worth writing down: named
  parameters are `@name`, **not** `$name` (PostgreSQL's own placeholder syntax
  reaches the server literally and fails with `syntax error at or near $`), and
  a multi-statement command cannot carry parameters at all - the DDL and the
  version insert are therefore separate commands.
