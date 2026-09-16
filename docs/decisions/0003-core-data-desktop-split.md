# 0003-core-data-desktop-split

- **Status:** Accepted
- **Date:** 2026-09-15

## Context

WinUI 3 is Windows-only. The brief allows future macOS/Linux/mobile
clients (Phase 13) but requires the Windows implementation not be
compromised now (see
[vision.md §3](../vision.md#3-desktop-application)). Business logic
written directly against WinUI would have to be rewritten entirely for any
future platform.

## Decision

Split the client into three projects:

- `MdBolsa.Core` — domain models, Markdown/wiki-link parsing, vault
  abstraction, indexing interfaces, sync/conflict contracts. Pure .NET,
  zero UI or platform dependencies.
- `MdBolsa.Data` — SQLite-backed implementation of Core's storage
  interfaces. Pure .NET.
- `MdBolsa.Desktop.WinUI` — the WinUI 3 shell. Composition root only: DI
  wiring, views/viewmodels, filesystem watcher. No business logic.

## Consequences

- Any future non-Windows shell reuses `Core` and `Data` unchanged and only
  rewrites the UI layer.
- Adds a small amount of upfront structure (three projects instead of one)
  in exchange for avoiding a full rewrite later — a deliberate, scoped
  exception to "don't build for hypothetical future requirements," since
  the brief explicitly names future clients as a real (if later) goal.
- Enforces that `MdBolsa.Desktop.WinUI` must never take on logic that
  belongs in `Core`/`Data` — reviewed at each phase boundary.
