# Architecture

This document describes the Phase 0 architecture decisions. See
[vision.md](vision.md) for the full product brief these decisions serve,
and [decisions/](decisions/) for the individual ADRs behind each choice.

## Layering

```
MdBolsa.Core       Domain models, Markdown/wiki-link parsing, vault
                    abstraction, indexing interfaces, sync/conflict
                    *contracts*. Pure .NET (net8.0). No UI, no platform
                    dependencies. Reusable by any future client shell.

MdBolsa.Data       SQLite-backed implementation of Core's storage
                    interfaces: note index, links/backlinks, tags,
                    search index, sync metadata. Pure .NET (net8.0).

MdBolsa.Desktop.WinUI   WinUI 3 shell (net8.0-windows10.0.26100.0, single
                    x64 platform — see note below). Composition root (DI
                    wiring), views/viewmodels, filesystem watcher.
                    Contains no business logic yet. Scaffolded in Phase 1
                    via the official `dotnet new winui3` CLI template
                    (Visual Studio is not required to build or run it).

MdBolsa.Server     ASP.NET Core sync/storage server. Created in Phase 8.
```

The Core/Data split from the WinUI shell exists specifically so a future
macOS/Linux/mobile client (Phase 13) can reuse Core and Data and only
rewrite the shell — WinUI 3 itself is Windows-only and cannot be shared.

## Repository Layout

```
mdbolsa/
├── docs/                      Architecture, dev/deploy/sync docs, ADRs
├── src/
│   ├── MdBolsa.sln
│   └── Client/
│       ├── MdBolsa.Core/
│       ├── MdBolsa.Data/
│       └── MdBolsa.Desktop.WinUI/   (placeholder until Phase 1)
├── tests/
│   ├── MdBolsa.Core.Tests/
│   └── MdBolsa.Data.Tests/
├── samples/dev-vault/         Small, fake vault used as the DEV default
└── config/                    Config/secret templates (`.example` files)
```

Single repo (monorepo) for client + future server: for a solo long-term
project, keeping shared contracts (sync DTOs, entity identifiers)
colocated avoids cross-repo versioning overhead. Revisit only if the
server grows independent contributors.

## Dependencies (Phase 0/near-term)

| Need | Choice | Why |
|---|---|---|
| Markdown parsing | Markdig | De facto .NET standard; extensible for `[[wiki links]]` and Mermaid fences (Phase 3+) |
| Local DB access | `Microsoft.Data.Sqlite` (raw ADO.NET) | No EF Core — this is an index cache, not a rich domain model. EF's change tracking and startup cost work against fast-startup/low-idle-RAM goals. |
| MVVM | `CommunityToolkit.Mvvm` | Source-generator based, near-zero runtime cost (Phase 1+) |
| DI | `Microsoft.Extensions.DependencyInjection` | Standard, no added cost |
| Testing | xUnit | Minimal; add FluentAssertions etc. only if it earns its keep |

Deliberately **not** adopted yet: EF Core, MediatR, AutoMapper, Serilog,
SignalR, gRPC. Every dependency must justify itself against: why is it
needed, can the platform already do this, what's the runtime cost, what's
the maintenance burden (see [vision.md §14](vision.md#14-development-philosophy)).

## Dev/Prod Isolation

- Separate app-data roots: DEV uses `%LOCALAPPDATA%\MdBolsa.Dev\`, PROD
  uses `%LOCALAPPDATA%\MdBolsa\` — different local SQLite index files and
  settings, independent of whether a server exists yet.
- `AppEnvironment` (Development/Production) is resolved at startup,
  defaulting to Development. Switching to Production requires an explicit
  local settings override — never a hardcoded flag baked into a build.
- DEV's default vault is [`samples/dev-vault/`](../samples/dev-vault). The
  real vault path is always a runtime setting — never hardcoded, never
  committed.
- From Phase 8/9 onward, "Production" means the sync client points at the
  Pi's configured URL. Same code path either way — only configuration
  differs. Consider an extra confirmation step before a build talks to a
  Production URL.

## Storage Path Configuration (microSD -> SSD)

All server-side file/DB paths derive from a single configurable root
(`APP_DATA_PATH`-style setting, `IOptions<StorageOptions>` in ASP.NET Core
once Phase 8 exists), mapped to a Docker bind mount. Moving from microSD to
SSD later is a bind-mount + data-copy operation, not a code change.

## Open Questions Flagged for Later Phases (not decided now)

- **Stable note identity.** Path-based identity breaks under
  rename/move across devices, which matters once sync (Phase 9) exists.
  Options: a stable ID in YAML frontmatter (portable, visible) vs.
  path+content-hash rename heuristics (closer to Obsidian's approach).
  Decide in Phase 2 when the vault/indexing schema is designed.
- **Where note history lives** (Phase 10): SQLite blobs, on-disk
  snapshots, or Pi-side history only. The local schema should reserve
  `revision`, `content_hash`, and `updated_at` columns from Phase 2 so this
  isn't a migration later.

## WinUI Project Notes (Phase 1)

- The template defaults to `<Platforms>x86;x64;ARM64</Platforms>`,
  narrowed to `x64` only — this is the only platform this project
  targets today, and multi-platform output paths (`bin\$(Platform)\...`)
  were also incompatible with the WinApp CLI's `dotnet run` path
  resolution as of `Microsoft.Windows.SDK.BuildTools.WinApp` 0.6.1
  (preview). Revisit if ARM64/x86 support is ever needed.
- Running the app locally (not just building it) requires **Windows
  Developer Mode** enabled (Settings → Privacy & security → For
  developers), since it launches as a debug-registered packaged app
  without a signed MSIX. See [development.md](development.md).

## Verified Working

- `dotnet build src/MdBolsa.sln` builds all five projects
  (`MdBolsa.Core`, `MdBolsa.Data`, `MdBolsa.Desktop.WinUI`, and their
  test projects) cleanly with only the .NET 8 SDK and Visual Studio Build
  Tools 2022 — no full Visual Studio IDE required.
- `dotnet test src/MdBolsa.sln` passes.
- MSIX debug-packaging (`winapp run`) succeeds through the build and
  manifest/package-identity steps.
- Actually launching the window is unverified on this machine pending
  Developer Mode being enabled (see above).
