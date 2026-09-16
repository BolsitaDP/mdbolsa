# 0001-local-first-architecture

- **Status:** Accepted
- **Date:** 2026-09-15

## Context

The application must remain fully usable without the Raspberry Pi or any
network connection — writing a note must never depend on a server. This is
the foundational principle of the whole project (see
[vision.md §2](../vision.md#2-fundamental-principle-local-first)).

## Decision

The desktop client owns note storage and indexing completely on its own.
The Raspberry Pi is a synchronization/storage/backup service, never the
primary runtime for editing. All heavy computation (Markdown rendering,
search indexing, graph layout, diagrams, canvas) happens client-side; the
server only stores and synchronizes.

## Consequences

- The desktop app must ship with its own local persistence (Markdown files
  + SQLite index) from Phase 2 onward, independent of any server work.
- The server (Phase 8+) can be designed later without blocking any editing
  feature.
- Sync becomes an additive background layer (Phase 9) rather than a
  dependency the editor waits on — this constrains sync to be async and
  incremental by construction.
