# 0017-local-backend-without-docker

- **Status:** Accepted
- **Date:** 2026-10-03

## Context

The backend (PostgreSQL + the API) is meant to live on the Raspberry Pi
([vision.md §6](../vision.md#6-raspberry-pi-server)). Until it does, development
needs the same thing running on the Windows machine - for the app's sync, and for the
27 end-to-end server tests that exercise sync, attachments, device tokens and history
and are only worth anything against a real database.

The documented way was Docker Desktop (`docker compose up -d`). On this machine
Docker Desktop 4.41.2 does not start: its backend exits at "initializing Inference
manager" on a stale `dockerInference` socket it cannot delete. Those 27 tests had been
skipped - and before that, silently counted as passed - for as long as that was true.
A development loop that depends on a tool that is down is not a loop, and "install
Docker and hope" is not a thing the repository should leave to chance.

Two constraints were already decided and are not reopened here:

- [0010](0010-server-foundation.md): PostgreSQL from day one, **not** SQLite while
  developing. Development has to run the same engine as the Pi, or the first real
  difference is found on the Pi.
- The repository lives in a OneDrive folder (`OneDrive\Escritorio`). A live database
  inside a synced folder gets its files locked and uploaded mid-write.

## Decision

`scripts/dev-backend.ps1` (wrapped by `backend.cmd`) runs a **portable PostgreSQL 16**
and the API from source, with no Docker and no administrator rights:

- **Same engine, same major version** as `postgres:16-alpine` in the compose files:
  EnterpriseDB's Windows binaries, pinned to an exact version and a SHA-256. A download
  that does not match is deleted and refused before anything is extracted or run.
- **Outside the repository**, under `%LOCALAPPDATA%\MdBolsa.Backend.Dev` (override with
  `MDBOLSA_BACKEND_HOME`): binaries, data, logs. Not in OneDrive, not in git.
- **Loopback only**: PostgreSQL on `127.0.0.1:5432`, the API on `localhost:5080`, with
  the same throwaway credentials as `docker-compose.yml` and `appsettings.Development.json`
  - which is also what the end-to-end tests expect. Nothing here is a production
  credential and nothing here is reachable from the network.
- **It only ever stops what it started.** The API's pid is recorded with its start time
  and checked against the process's command line before it is stopped; PostgreSQL is
  stopped with `pg_ctl`. A process merely *listening* on a port is reported and left
  alone, because on this machine a port can belong to WSL2 and to unrelated work.
- `start`, `stop`, `status`, and `reset -Force` (delete the local database; refuses
  without the switch). `start` is idempotent.

Docker stays: `docker-compose.yml` is still the way to run the same shape as the Pi,
and `docker-compose.prod.yml` is still the Pi's deployment. This is an *additional*
way to develop, not a replacement for either.

## Consequences

- The 27 end-to-end tests can run on this machine again (`MDBOLSA_REQUIRE_SERVER=1
  dotnet test tests/MdBolsa.Server.Tests`), and when they do the result means something.
- **It is not the Pi's environment.** It is a Windows build of PostgreSQL, initialised
  with the `C` locale, against Docker's Linux build. The version matches; the OS,
  the filesystem and the collation do not. This proves the SQL and the API work on
  PostgreSQL 16 - it does **not** prove `docker-compose.prod.yml` works, and that file
  has still never been brought up.
- **The integrity check is weaker than it sounds.** The EnterpriseDB zip is *not*
  Authenticode-signed (`postgres.exe` reports `NotSigned`), and no checksum was found
  for it (the obvious `.sha256` / `.md5` / `SHA256SUMS` locations next to the file return
  403). The pinned SHA-256 was computed from the first download over HTTPS from
  EnterpriseDB's own host - trust on first use. It protects against
  corruption and against the file changing later; it does not protect against the file
  having been wrong the first time. Bumping the version means re-pinning the hash, by
  hand, on purpose.
- First start downloads about 300 MB. After extraction the archive is deleted; the
  install is about 120 MB.
- Docker's PostgreSQL and this one are **separate databases** that both want port 5432:
  notes synced into one are not in the other, and only one can run at a time (the
  script says so rather than failing obscurely).
- Data checksums are off, as in the default Docker image. This is a development database.

## Considered and not chosen

- **Fix Docker Desktop.** Worth doing for the Pi-shaped workflow, but it was a failed
  attempt on this machine and this decision should not wait on it.
- **SQLite for local development.** Ruled out by 0010, and the e2e tests would then test
  a different database from the one in production.
- **PostgreSQL inside WSL.** That distribution hosts unrelated projects; this repository
  should not install things into it.
- **A system-wide PostgreSQL installer.** Needs administrator rights, installs a
  service that starts with Windows, and is a lot to ask for a development dependency.
