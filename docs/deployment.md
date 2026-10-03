# Deployment

The Raspberry Pi runs the sync/storage server ([vision.md §6](../vision.md#6-raspberry-pi-server)).
This document covers the development stack, the Pi deployment, and the
microSD → SSD migration. Design decisions live in
[0010-server-foundation](decisions/0010-server-foundation.md).

## Development (Windows)

Two pieces, deliberately run separately:

```bash
# 1. the database (Docker)
docker compose up -d          # Postgres 16, published on 127.0.0.1:5432 only

# 2. the API (from source, so a rebuild is a normal build)
dotnet run --project src/Server/MdBolsa.Server
```

No Docker (or Docker Desktop won't start)? `backend.cmd` runs the same two pieces -
a portable PostgreSQL 16 and the API - directly on Windows; see
[development.md](development.md#run-the-backend-locally) and
[0017](decisions/0017-local-backend-without-docker.md). It is for development only:
the Pi runs `docker-compose.prod.yml`, below.

The API listens on <http://localhost:5080>. `GET /health` reports the
environment, the database it resolved, the schema version, and which
authentication is in force:

```json
{"status":"ok","environment":"Development","database":"localhost/mdbolsa_dev",
 "schemaVersion":"3","authentication":"shared token (development default) + per-device tokens"}
```

The schema is created on boot, so there is no migration step to run.

## Adding a device, and taking one away

The shared token (`Sync:Token`, or the `Sync__Token` environment variable) is the
only credential that can manage devices. Everything below needs it, and a device
token gets **401** on all of it - one stolen laptop must not be able to hand
itself a permanent key.

In development the shared token is the throwaway one in
`src/Server/MdBolsa.Server/appsettings.Development.json`:

```bash
export MDBOLSA_TOKEN=dev-token-not-a-secret
```

Give a machine its own token:

```bash
# one-time: paste this into the app's sync settings and press
# "Create this device's token"
curl -X POST http://localhost:5080/api/devices \
  -H "X-MdBolsa-Token: $MDBOLSA_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"work laptop"}'
```

The response is the **only** time the token exists outside that machine: the
server kept `sha256(token)` and cannot reproduce it. Lose it, and the answer is
to revoke and mint another - not to look it up.

See what has access, and when each was last here:

```bash
curl http://localhost:5080/api/devices -H "X-MdBolsa-Token: $MDBOLSA_TOKEN"
```

```json
[{"id":"...","name":"work laptop","createdAt":"...","lastSeenAt":"...","revoked":false}]
```

Take one away. It stops working on the next request, and no other device is
affected:

```bash
curl -X DELETE http://localhost:5080/api/devices/$ID -H "X-MdBolsa-Token: $MDBOLSA_TOKEN"
```

Revoked devices stay in the list, marked revoked, so "which of these was the
phone I gave away" has an answer. `lastSeenAt` is written at most once every
five minutes: it answers "when was this machine last here", not "when did this
request happen".


Verify the API end to end. `/health` needs no token; every `/api/...` route does
(a missing or wrong one is a **401**), so each call below carries
`X-MdBolsa-Token`:

```bash
# health
curl http://localhost:5080/health

# store a note (the ids are the note's frontmatter id and the acting device)
curl -X PUT http://localhost:5080/api/notes/<guid> \
  -H "X-MdBolsa-Token: $MDBOLSA_TOKEN" -H 'Content-Type: application/json' -d '{
  "id": "<guid>", "relativePath": "Personal/Home.md", "title": "Home",
  "content": "# Home", "contentHash": "HASH", "revision": 1,
  "deviceId": "<guid>", "updatedAt": "2026-09-26T12:00:00Z"
}'

# read it back
curl http://localhost:5080/api/notes/<guid> -H "X-MdBolsa-Token: $MDBOLSA_TOKEN"

# everything that changed since a point, oldest first, paged
curl "http://localhost:5080/api/notes/changes?since=2026-01-01T00:00:00Z&limit=200" \
  -H "X-MdBolsa-Token: $MDBOLSA_TOKEN"

# delete (tombstone, not a hard delete)
curl -X DELETE http://localhost:5080/api/notes/<guid> \
  -H "X-MdBolsa-Token: $MDBOLSA_TOKEN" -H 'X-MdBolsa-Device: <guid>'
```

`nextCursor` from `/changes` is passed back as `cursorAt` + `cursorId` to get the
next page.

## Raspberry Pi (production)

Nothing about production is committed: no credentials, no hostnames, no IPs
([vision.md §9](../vision.md#9-configuration)). Deploy is explicit, from a
tagged commit on `main` — never automatic
([vision.md §8](../vision.md#8-git-workflow)).

1. Copy the repository to the Pi at the tag you want to deploy.
2. Create a `.env` next to the compose file with real values (see below).
3. `docker compose -f docker-compose.prod.yml up -d --build`.

The API image is built from the `Dockerfile` in this repo (multi-stage: SDK to
build, ASP.NET runtime to run, non-root). The development compose file only runs
Postgres, because in development the API runs from source.

Required environment (all of them, every one):

| Variable | Meaning |
|---|---|
| `APP_DATA_PATH` | The single root for all persistent state. The microSD → SSD migration point. |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | Real credentials, generated for the Pi. |
| `ConnectionStrings__Postgres` | Full Npgsql connection string the API uses. |
| `Sync__Token` | The shared (bootstrap) token. Generate a long random value for the Pi; never reuse the development one. |

Three properties are enforced, not documented-and-hoped-for:

- **The API refuses to start in Production without `ConnectionStrings__Postgres`.**
  There is no default. A development build cannot reach production by accident.
- **The API refuses to start in Production without `Sync__Token`.** An
  unauthenticated note store is not a production configuration, so there is no
  default and no "open" mode. `docker-compose.prod.yml` requires the variable too,
  so a missing one fails at `docker compose up` rather than as a container that
  starts and exits.
- **Nothing is published to the network until you decide to.** The API is
  authenticated ([0011](decisions/0011-client-sync.md), and revocable per device
  since [0014](decisions/0014-per-device-tokens.md)), but it speaks plain HTTP, so
  a token crosses the wire in the clear. On a home network, keep it bound to
  loopback and reach it over a VPN or an SSH tunnel; if it must be reachable
  otherwise, put a reverse proxy with TLS in front of it first. Do not
  port-forward it to the internet as it stands.

Once it is up, use the shared token once to give each machine a token of its own
(see "Adding a device, and taking one away" above), and keep the shared one out of
the clients.

## microSD → SSD migration

Because everything persistent hangs off `APP_DATA_PATH`:

1. Stop the stack: `docker compose down`.
2. Copy the data directory to the SSD, preserving ownership.
3. Point `APP_DATA_PATH` at the new location.
4. `docker compose up -d`.

No code change, no rebuild, no schema migration — which is the requirement in
[vision.md §6](../vision.md#6-raspberry-pi-server).

## Backups

Not implemented (Phase 14). The shape is already decided by the layout above:
`pg_dump` on a schedule into a directory under `APP_DATA_PATH`, kept off the Pi.
Worth noting now: the local `.md` files on each client remain the source of truth
([0002](decisions/0002-markdown-as-canonical-storage.md)), so a server backup is
about not losing sync state and history, not about the only copy of your notes.
