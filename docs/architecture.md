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

- **Where note history lives** (Phase 10): SQLite blobs, on-disk
  snapshots, or Pi-side history only. The local schema already reserves
  `revision`, `content_hash`, and `updated_at` columns (Phase 2) so this
  isn't a migration later.

## Vault Indexing (Phase 2)

Stable note identity resolved: see
[0005-stable-note-identity](decisions/0005-stable-note-identity.md).
Concretely:

- `MdBolsa.Core.Vault`: `NoteMetadata`, `IVaultIndex` (the storage
  interface), `FrontMatter` (minimal `id:` read/inject, deliberately not
  a full YAML parser), and `VaultScanner` (walks `*.md` files, assigns
  ids, hashes content, diffs against the existing index to produce
  added/updated/moved/deleted/unchanged counts).
- `MdBolsa.Data.Vault.SqliteVaultIndex` implements `IVaultIndex` against
  a `notes` table (`id` primary key, `path`, `title`, `content_hash`,
  `revision`, `created_at`, `updated_at`). Schema created via
  `CREATE TABLE IF NOT EXISTS` on construction — no migration framework
  yet; one is only worth building once a later phase needs to *alter*
  an existing table shape, not just add new tables.
- No new NuGet dependencies were needed (`Guid`, `SHA256` are BCL).
  Markdig/YamlDotNet/DI are still deferred until Phase 3+ actually needs
  them (see Dependencies table above).
- The WinUI shell exercises this for real: a text box for a vault path
  plus an "Open vault" button that runs `VaultScanner` against
  `SqliteVaultIndex` and lists what it found.

## Wiki Links & Backlinks (Phase 4)

- `MdBolsa.Core.Links`: `NoteLink`, `ILinkIndex` (storage interface),
  `WikiLinkParser` (regex-based `[[Target]]` / `[[Target|Display]]` /
  `[[Target#Heading]]` extraction — no Markdig yet; nothing else about a
  note's Markdown structure matters for this), and `LinkScanner` (reads
  every indexed note, extracts targets, resolves them against the
  current vault, and replaces that note's link rows). Resolution rule:
  see [0006-wiki-link-resolution](decisions/0006-wiki-link-resolution.md).
- `MdBolsa.Data.Links.SqliteLinkIndex` implements `ILinkIndex` against a
  `links` table (`source_note_id`, `target_text`,
  `target_note_id` nullable — unresolved links are kept, not dropped, so
  they can eventually be shown as "broken"). Same `CREATE TABLE IF NOT
  EXISTS` approach as `notes`, same SQLite file.
- `LinkScanner` runs after `VaultScanner` in the WinUI shell (it needs
  the current note set to resolve targets against) - both are driven
  from the same "Open vault" action.
- The WinUI shell now combines this with the (unblocked, see below)
  Phase 3 editor: the same `Editor` `TextBox` used to type a note is
  what displays it, with the backlinks panel underneath, refreshed on
  every note switch.

## Search (Phase 5)

- `MdBolsa.Core.Search`: `SearchResult`, `ISearchIndex` (storage
  interface), `SearchScanner` (reindexes every note's title + body,
  stripped of frontmatter via `FrontMatter.Body`, on every scan).
- `MdBolsa.Data.Search.SqliteSearchIndex` implements `ISearchIndex`
  against a `notes_fts` **FTS5 virtual table** - SQLite's built-in
  full-text search, bundled with the SQLite native library already in
  use, so no new NuGet dependency. See
  [0007-search-with-fts5](decisions/0007-search-with-fts5.md) for why,
  and for the query-sanitization approach (a search box never sees raw
  FTS5 query syntax - each term becomes a quoted, ANDed phrase).
- The WinUI shell adds a search box + "Search"/"Show all notes"
  buttons above the notes list; a search result replaces the list with
  matching notes (title + snippet), each still clickable to open.
  Deliberately button-triggered, not `TextChanged`-driven - see the
  `MainPage` class comment.

## Known Issues (Resolved)

- **WinUI 3 `TextBox.Text` getter crashed the process natively**, on
  this machine's WindowsAppSDK 2.4.0 (preview) build, when reading back
  text that was set programmatically. Confirmed via Windows Event Log:
  `STATUS_STOWED_EXCEPTION` (`0xc000027b`), faulting module varied
  (`Microsoft.UI.Xaml.dll`, `combase.dll`, `CoreMessagingXP.dll` seen
  across different runs) - not a catchable .NET exception, so
  `try/catch` around the call site did not help. Reproduced with: a
  no-op `TextChanged` handler, a polling `DispatcherQueueTimer`,
  `GetValue(TextBox.TextProperty)` instead of `.Text`, and
  `RichEditBox.Document.GetText` instead of `TextBox`, including with a
  literally empty event handler touching neither the filesystem nor
  SQLite.
  **What made it stop reproducing:** normalizing CRLF/CR to LF before
  writing `Editor.Text` to disk, and making `SqliteVaultIndex.Upsert`
  tolerant of a stale row at the same path under a different id (both in
  `MainPage.SaveCurrentNote` / `SqliteVaultIndex.cs`). This was applied
  on a theory that the "crash" was actually an uncaught
  `SqliteException` from a path/id unique-constraint violation getting
  misreported as a native fault - that theory doesn't fully square with
  the empty-handler reproduction above, so **the root cause is not
  confidently identified**, but saving has since been verified live
  and repeatedly (open a note, type a real edit including adding a new
  `[[wiki link]]`, click Save) with no crash and correct round-tripping
  on disk (single `id:`, LF line endings, new link resolved and
  deduplicated in backlinks). If this resurfaces, the isolation notes
  above are the starting point, and reverting to a read-only viewer
  (`TextBlock.Text` set-only, never read back - proven completely
  stable throughout this investigation) is the known-safe fallback.

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
- Plain `dotnet run` does not perform debug package-identity
  registration correctly for this (preview) tooling — it launches the
  raw `.exe` directly, which fails with `REGDB_E_CLASSNOTREG` even with
  Developer Mode on. Use the `winapp` CLI (`Microsoft.Windows.SDK.BuildTools.WinApp`)
  to run instead; it builds, installs any missing Windows App Runtime
  MSIX packages, registers a loose-layout package, and launches via
  AUMID. See [development.md](development.md) for the exact command.
- Packaged apps get `%LOCALAPPDATA%` (and similar special folders)
  silently redirected to a per-package virtualized location
  (`%LOCALAPPDATA%\Packages\<PackageFamilyName>\...`), even though this
  one runs `runFullTrust` and isn't AppContainer-sandboxed. This is
  invisible from *inside* the app (`Environment.GetFolderPath` "just
  works" and is consistent across the app's own reads/writes), but it
  means nothing written from *outside* the packaged process (e.g. a
  config file dropped there by a script or by us during manual testing)
  is visible to the app. Don't rely on external processes writing into
  the packaged app's `%LOCALAPPDATA%` - drive configuration through the
  app's own UI instead (see the vault path text box in Phase 2).

## Verified Working

- `dotnet build src/MdBolsa.sln` / `dotnet test src/MdBolsa.sln` pass for
  all five projects, using only the .NET 8 SDK and Visual Studio Build
  Tools 2022 — no full Visual Studio IDE required.
- The WinUI 3 shell actually launches: confirmed a live process with
  `MainWindowTitle` "mdbolsa" and ~120MB working set on a Debug build
  (2026-09-16).
- Phase 4 (wiki links/backlinks) and the Phase 3 editor's save path are
  both verified live, together, in the merged shell: open vault → open
  a note → see its backlinks → type an edit adding a new `[[link]]` →
  Save → no crash → file round-trips correctly on disk → the new
  link's backlink appears, correctly deduplicated, on the target note.
- Phase 5 (search) verified live: searching a term only in a note's
  frontmatter correctly returns zero results (frontmatter is excluded
  by design); searching a term in a note's body returns it with a
  correct snippet; clicking a result opens that note in the editor;
  "Show all notes" correctly resets the list.
- 51 tests pass (34 Core, 17 Data).
