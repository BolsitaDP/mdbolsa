# Synchronization

The client and server both exist: Phase 8 built the API
([0010](decisions/0010-server-foundation.md)) and Phase 9 made the client speak
to it ([0011](decisions/0011-client-sync.md)). This document is the contract
between them.

## Philosophy (unchanged, and still binding)

- **Local-first**: writing a note never depends on the server or the network.
  Saving does no network work at all - sync happens when you press the button.
- **Incremental**: never re-upload the whole vault. `GET /api/notes/changes`
  answers "everything that changed since X", and the client only sends notes
  whose content hash differs from what the server last confirmed.
- **Asynchronous**: typing never waits on sync. Debounce-after-edit, periodic
  sync and sync-on-reconnect remain open triggers ([vision.md §10](vision.md#10-synchronization-philosophy));
  none of them need a design change, only a different caller of `SyncAsync`.

## Using it

1. Start the server: `docker compose up -d`, then
   `dotnet run --project src/Server/MdBolsa.Server`. The development token is in
   `appsettings.Development.json` and must match what the client is given.
2. In the app: **Sync settings...**, enter the server URL
   (`http://localhost:5080`) and the token, **Save**.
3. **Sync**. The status bar reports what moved in each direction.

The device id is generated on first use and kept in the app's settings; it is
how the server records who wrote last.

## The contract

One row per note. `id`, `content_hash`, `revision`, `device_id`, `updated_at`,
and `deleted_at` as a tombstone (see [0010](decisions/0010-server-foundation.md)).

| | |
|---|---|
| `GET /health` | Open. Reports environment, database, schema version, and which auth is in force |
| `GET /api/notes/changes` | Incremental feed, paged by an `(updated_at, id)` watermark |
| `GET /api/notes/{id}` | One note |
| `PUT /api/notes/{id}` | Store a change; **409** if refused, with the stored state |
| `DELETE /api/notes/{id}` | Tombstone; needs `X-MdBolsa-Device` |
| `GET /api/attachments` | Known attachments, newest first |
| `PUT /api/attachments/{hash}` | Store bytes, verified against the hash in the path |
| `GET /api/attachments/{hash}` | The bytes |
| `POST /api/devices` | Mint a device token. **Shared token only** |
| `GET /api/devices` | Every device, revoked included. **Shared token only** |
| `DELETE /api/devices/{id}` | Revoke a device. **Shared token only** |

Two headers on everything under `/api/notes` and `/api/attachments`:
`X-MdBolsa-Token` and `X-MdBolsa-Device`.

## Two tokens, and which is which

`X-MdBolsa-Token` carries either of two things, and the server tells them apart
by shape, not by asking:

- **The shared token** (`Sync:Token`). Vault-wide, never leaves the owner's
  hands, and the *only* credential allowed to mint or revoke a device.
- **A device token** (`mdb_` + 32 hex). Issued once per device, stored by the
  server as a hash only, and revocable on its own.

So a client configured with the shared token keeps working untouched, and a
client that has exchanged it for a device token can be shut off without touching
anything else. In the app, the exchange is one button in sync settings: paste the
shared token, press **Create this device's token**, and the box is replaced with
one only that machine can use. See
[0014](decisions/0014-per-device-tokens.md) for what this deliberately leaves
out (no expiry, no scopes, no accounts).

A device token **cannot** mint or revoke devices. That is the property that makes
the phase worth anything, and it is enforced on a separate code path from
ordinary authentication on purpose.


## What the client does, in order

1. **Rescan** the vault, so the push side works from a current note list.
2. **Pull.** For each change: write it if this device hasn't touched the note
   since the server last confirmed it, delete the file if it's a tombstone, and
   *record a conflict and touch nothing* if both sides changed.
3. **Push** every note whose hash differs from the server's, and record the
   conflict when the server answers 409.
4. **Rescan** again - the pull may have written or deleted files.

"Has this device touched it?" is answered by comparing content hashes, not
timestamps, so a moved clock doesn't make a note look edited. The hash is
recorded only after the server accepts a write.

## Conflicts: choose a version, never merge

A note changed in two places is left alone on both sides and recorded. The
**Conflicts** button in the shell lists them, and each one offers the only two
honest choices:

- **Keep mine** - your version is pushed over the server's, with a fresh
  timestamp. The server's version becomes history, not rubble.
- **Take theirs** - the server's version is written over your file. Nothing is
  sent; it already has that content.

Until you choose, the note stays out of sync **in both directions**, which is
the safe direction to be wrong in.

**Nothing is ever merged automatically.** A real merge needs a common ancestor
and Markdown-aware diffing, and a wrong merge produces a third version neither
person wrote while quietly destroying the disagreement. That could be a later
phase with its own ADR, but it should be asked for by someone who has hit a real
merge.

### History

Every accepted write is kept by the server, so the version you didn't pick is
always there:

```
GET /api/notes/{id}/versions        newest first
GET /api/notes/{id}/versions/{rev}   one revision's content
```

## Attachments

Files in `Attachments/` are identified by the SHA-256 of their contents, and the file
is named after it. Identical bytes are therefore the same attachment on every
device, and **an attachment cannot conflict** - there is one possible content for a
given hash, so there is nothing for two devices to disagree about.

```
PUT /api/attachments/{hash}            upload (the hash is verified, not trusted)
GET /api/attachments/{hash}            download
GET /api/attachments/changes?since=    the incremental pull
GET /api/attachments                   what is stored, names and sizes
```

The hash in the URL is checked against the body: a mismatch is 422 and nothing is
stored, because a server that believed the client would spread corruption to every
device that asked. Uploads are capped at 32 MB. A rejected upload may surface as a
413 or as a connection closed mid-send, depending on the server - either way nothing
is stored, which is the property that matters.

## Still open

- **Triggers**: sync is manual today. Debounce after edits, periodic, on
  reconnect - all just different callers of the same `SyncAsync`.
- **Authentication**: one shared token is enough for a home network and not for
  anything else. Per-device tokens with rotation are the obvious next step.
- **Attachments** (Phase 11) and their own sync story.
