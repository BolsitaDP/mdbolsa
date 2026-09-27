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

## Tags & Metadata (Phase 6)

- `MdBolsa.Core.Tags`: `TagCount`, `ITagIndex` (storage interface),
  `TagParser` (frontmatter `tags:` — flow, scalar and block-sequence forms —
  plus inline `#hashtags`, with headings/code/URL fragments explicitly excluded;
  no Markdig), and `TagScanner` (reindexes every note's tags on every scan and
  replaces each note's rows wholesale, like `LinkScanner`/`SearchScanner`).
  Rules and normalization: see
  [0008-tags-and-metadata](decisions/0008-tags-and-metadata.md).
- `MdBolsa.Data.Tags.SqliteTagIndex` implements `ITagIndex` against a
  `note_tags (note_id, tag)` table — deliberately no separate tag table, so a
  tag can't outlive the last note using it; per-tag counts are a `GROUP BY`.
  Same `CREATE TABLE IF NOT EXISTS` approach, same SQLite file as every other
  index.
- `FrontMatter` gained two read-only members for this phase: `TryReadBlock`
  (the raw block, so `TagParser` can read `tags:` lines) and `ReadFields`
  (top-level `key: value` pairs, for display). It still has exactly one write
  path — injecting `id:` — so the app still never rewrites YAML it doesn't own.
- The WinUI shell adds a tag panel above the notes list: every tag in the
  vault with its note count, click to filter the notes list, click again to
  clear. A read-only metadata line under the editor shows the note's path,
  revision, timestamps, its tags, and any other top-level frontmatter fields.
  Both are click-driven, never `TextChanged`-driven — see the `MainPage` class
  comment.

## Knowledge Graph (Phase 7)

- `MdBolsa.Core.Graph`: `GraphNode`/`GraphEdge`/`GraphModel` (pure data - no
  coordinates, no colours), `GraphBuilder` (derives the whole graph from the
  notes/links/tags indexes, and the note-local graph as a breadth-first
  expansion over that same graph), and `GraphLayout` (deterministic
  Fruchterman-Reingold force-directed layout, no RNG, in Core so it's testable
  and reusable by future clients). Rules: see
  [0009-local-graph](decisions/0009-local-graph.md).
- No new table, no new scanner and no new dependency: the graph is a derived
  view of the three indexes the other features already read. Unresolved links
  are carried as a count rather than drawn as nodes, and self-links are skipped.
- The WinUI shell adds a second page, `GraphPage`, reachable from a "Graph"
  button: whole-vault or note-local scope, a center note and depth selector,
  nodes as `Ellipse`s / edges as `Line`s on a `Canvas`, drag to pan, buttons to
  zoom, click a node to see what it is and re-center on it. The graph is laid
  out in a fixed 1000x800 virtual space and mapped into the window by a
  scale/translate `RenderTransform`, so resizing doesn't relayout.
- The vault path, the SQLite index factories and the last-opened note moved into
  a small static `AppSession` shared by the two pages. Deliberately not a DI
  container - see the dependency table above.

## Server Foundation (Phase 8)

- `src/Server/MdBolsa.Server`: ASP.NET Core minimal API over PostgreSQL via raw
  `Npgsql` (no ORM, same call as the client SQLite indexes). One `notes` table
  carrying the sync metadata vision.md asks for in section 10 - `id`,
  `content_hash`, `revision`, `device_id`, `updated_at` - plus `deleted_at`
  tombstones, because a hard-deleted row is indistinguishable from a note that
  was never synced. Endpoints: `GET/PUT/DELETE /api/notes/{id}` and
  `GET /api/notes/changes` ("everything since X", paged by an
  `(updated_at, id)` watermark). Rules: see
  [0010-server-foundation](decisions/0010-server-foundation.md).
- The server stores notes byte-for-byte and never parses Markdown or computes
  hashes: the `.md` file stays the source of truth and the client stays the only
  thing that understands it.
- **No authentication**, explicitly and temporarily. What makes that safe today
  is that nothing exposes the API: it binds to `localhost:5080` in development,
  and the compose files publish Postgres on `127.0.0.1` only. Adding auth is the
  first thing before the Pi, and it is Phase 9 first job.
- Dev/Prod isolation is enforced in code, not by discipline: **in Production the
  server refuses to start** without an explicit `ConnectionStrings:Postgres`, so
  a development build cannot reach production by accident. Environment and
  database host (never the password) are logged at startup and on `/health`.
- `docker-compose.yml` (dev: Postgres only - the API runs from source so a
  rebuild is a normal build), `docker-compose.prod.yml` (Pi: Postgres + API,
  database not published, API on loopback), `Dockerfile` (multi-stage,
  non-root). All persistent state hangs off one configurable `APP_DATA_PATH`, so
  the microSD to SSD move is a data copy plus an environment variable.
- 14 tests cover `NoteValidation` (the rules with decisions in them).
  `NoteStore` itself is not unit-tested - it needs a real PostgreSQL;
  integration tests against the compose database are a Phase 9 item and should
  replace the hand-run `curl` sequence that verified this phase.
- Two Npgsql details that cost time here: named parameters are `@name`, **not**
  `$name` (PostgreSQL own placeholder syntax reaches the server literally and
  fails with "syntax error at or near $"), and a multi-statement command cannot
  carry parameters at all.

## Client Sync (Phase 9)

- `src/Shared/MdBolsa.Contracts`: the sync wire contract (DTOs + header names),
  referenced by both the server and `MdBolsa.Core`. Phase 8's DTOs were the
  server's own; two hand-written copies of a sync contract is drift that only
  shows up in production.
- `MdBolsa.Core.Sync`: `NoteSyncClient`, `ISyncStateStore`, `SyncResult`,
  `SyncConflict`. Two properties matter more than the rest:
  - **Pull happens before push.** A client that pushes first can't tell a stale
    local copy from a fresh one, and would overwrite a newer remote edit without
    seeing it.
  - **"Changed here since the server last saw it" is a hash comparison, not a
    timestamp** (the hash the server last confirmed is kept in the sync state
    store). A moved clock doesn't make a note look edited, and a note that only
    changed over there is unambiguously safe to overwrite locally.
- `MdBolsa.Data.Sync.SqliteSyncStateStore`: cursor, per-note pushed hash, and
  recorded conflicts, in the same SQLite file as the other local indexes. All of
  it is rebuildable - the `.md` files are the source of truth. The device id is
  per *machine*, so it lives in the app's settings store instead.
- Authentication: one shared token in `X-MdBolsa-Token`, compared on hashes in
  constant time. `/health` stays open and says which auth is in force. In
  Production the server refuses to start without a token.
- The shell adds a **Sync** button and an inline **Sync settings** panel (no
  dialog - popups crash this runtime). Saving a note does no network work at
  all: typing never waits on a round trip, per vision.md §10.
- Conflicts are **detected and reported, never resolved**: both versions are
  left alone and the note is recorded. `PUT` returns 409 with the stored state
  so a client can tell it lost a race, and a path collision is translated from a
  PostgreSQL unique-violation into that same clean 409. See
  [0011-client-sync](decisions/0011-client-sync.md).
- 18 client unit tests (fake HTTP handler, no network) and 6 end-to-end tests
  against a real server and PostgreSQL, which return early when the dev stack
  isn't running.
## Conflicts & Version History (Phase 10)

- `note_versions` on the server: every accepted write leaves a copy, written in
  the same transaction as the write that supersedes it - so there is never a
  moment where the old content is gone and the history row hasn't landed, and a
  refused write leaves no version behind. Nothing is pruned. Deletes archive too,
  which makes an accidental deletion recoverable. Two new reads:
  `GET /api/notes/{id}/versions` and `.../versions/{revision}`.
- `MdBolsa.Core.Sync.ConflictResolver`: **keep mine** (push the local copy with
  a fresh timestamp - no special endpoint needed, the server's newer-wins rule
  *is* the takeover, and the replaced version is already archived) and **take
  theirs** (fetch the server's copy, write it over the local file, record its
  hash so the next sync doesn't re-conflict). No write to the server in the
  second case: it already has that content.
- **No automatic merging**, and that is the decision worth defending. A real
  merge needs a common ancestor and Markdown-aware diffing; a wrong merge
  produces a third version neither person wrote and destroys the disagreement
  quietly. Until a conflict is resolved the note stays out of sync in both
  directions. See [0012-conflict-resolution](decisions/0012-conflict-resolution.md).
- A path collision is a 409 like a stale write, not a 500 - `NotePathConflictException`
  existed from Phase 9 and wasn't mapped until the end-to-end tests hit it.
- `ISyncStateStore.ResetCursor()` forgets the sync watermark so the next sync
  re-reads everything: the recovery path for a cursor that has drifted.
- **Restore is not undo.** `ConflictResolver.RestoreAsync` writes an earlier
  revision back as the note's *current* content, so the next sync pushes it as a new
  revision and the history grows rather than rewrites. Rewinding the server's
  history would throw away every edit made since, which is the opposite of what
  someone who clicked "restore revision 4" is asking for. The one exception is
  restoring the revision the server currently holds: that genuinely puts the two
  back in agreement, so it records the hash and clears the conflict. Any other
  restore leaves the note "changed here", because it is.
- The history is in the **right sidebar**, not the left one. It is context for the
  open note, not a view of the vault, and the left sidebar's single content area
  belongs to the file list. Loading it is a network read, so opening a note kicks
  it off rather than waiting for it - which means two loads can be in flight, and
  a generation counter drops the stale one. Without it, switching notes quickly
  appends both responses and you get two of every revision.
### Automatic sync

Sync was a button, which means a second device's edits arrived when you happened
to think to ask. There are now two triggers, and **neither is on the save path** -
a note is already on disk before either of them looks at it, and both run off the
dispatcher, so typing never waits for the network:

- **A ticker** wakes every 30 seconds and asks `SyncTrigger.Decide` whether the
  5-minute interval has elapsed. The decision is a few comparisons and **no
  network** - a tick with nothing to do costs nothing and touches nothing.
- **A debounce** is a one-shot timer rearmed on every write this app makes, so a
  burst of saves is one sync rather than ten. Rearming a timer is the entire rule;
  there is nothing to test.
- **Once at startup**, when a vault opens, so another device's edit is already
  here when you start working.

A failure **pauses** automatic sync instead of backing off and retrying. A server
that isn't there yet - the Pi is off, the network is down, a token was rotated -
will still not be there in four minutes, and a background task failing every thirty
seconds fills the status line with noise nobody asked for. The manual button stays,
because a person who has just started the server *wants* the retry, and a manual
success is what lifts the pause. Eight tests cover the rules; the shell only owns
the clock.

The re-entrancy rule from the Known Issues applies here too: `_syncRunning` stops a
tick from starting a second sync on top of the first.- The shell shows unresolved conflicts inline with the two buttons per note
  (again: no dialogs on this runtime).
## The shell (Obsidian-shaped)

The window is a deliberate imitation of Obsidian's: a narrow icon ribbon, the
file tree on the left, the note in the middle, the note's context on the right, a
status bar along the bottom, and `Ctrl+F` / `Ctrl+S` / `Ctrl+B` / `Ctrl+I` /
`Ctrl+G` / `Ctrl+P`. None of that is original, and that is the point - a tool
someone uses all day should be one they already know how to drive.

- `VaultTree` (Core) turns the relative paths the vault index already holds into a
  folder tree: folders exist only because a note lives under them, folders sort
  before notes, everything case-insensitively, and `.md` is stripped from what is
  shown. It is pure data so it can be tested without a UI; the shell decides how a
  node looks. 14 tests.
- The tree is rendered **flat**, one row per node with an indent per level, rather
  than as nested `TreeView`s. Two reasons, both practical: a row has to be
  swappable in and out for the inline rename, which means it has to be a direct
  child of the panel holding it; and a collapsed folder's rows are never built at
  all, which is what keeps a large vault from creating a control per hidden row.
- The sidebar has **one** content area and swaps between four views - the tree, a
  search or tag result, the sync settings, the conflicts - rather than stacking
  panels. Nothing has to agree about space, and there is no fifth thing to
  remember to hide.
- Colours come from theme resources, so the shell follows the system light/dark
  setting. Two places needed care: a theme brush **cannot** be looked up from
  `Application.Current.Resources` in code (only what the app declares itself is
  reachable there), so the selected-row state is a *style* declared in App.xaml;
  and `Path.Data` is a `Geometry`, not a string, so the folder chevron is built
  with `PathFigure`/`LineSegment`.
- Icons are hand-drawn geometry, not a symbol font. A missing or renumbered glyph
  renders as a tofu box, and there is no way to see that from here. Nothing uses an
  arc: a circle drawn as two identical arc commands is a trick that parses and then
  takes the renderer down.

### Search and the quick switcher

FTS5 already ranked by bm25 and indexed titles, so what was missing was not
relevance - it was that **FTS cannot find a note by its name**. Someone opening a
note they already know the name of is not looking for words they remember from
inside it, and a tool that makes them do that has misunderstood the request.

- `NoteNameMatcher` (Core, 21 tests) ranks name matches **exact, prefix, word-start,
  anywhere**, and stops there on purpose. A fuzzy subsequence matcher scores almost
  anything three letters long as a near-match, which makes the top result a coin
  toss - and the whole point of a switcher is that the first result is the one you
  meant, because you press Enter without reading. Six confident results beat sixty
  speculative ones.
- Accents and case are folded **for matching only**. "informacion" has to find
  "Información", because filenames here are written without accents most of the
  time, so that is what people type. Folding changes the *kind* of match too, which
  is correct: a query of "informacion" against a note called `informacion.md` is an
  exact match, not a prefix.
- The switcher is the sidebar's search view in a different mood: name matches
  first, the text index as the fallback, and never the same note twice. With nothing
  typed it shows what you have been reading, which is what a switcher opened with no
  intention in mind is usually for.
- The search field has a **submit button**, not just Enter. This is not decoration:
  the search deliberately does not filter as you type, because rebuilding a list of
  controls from inside a text input's own event is one of the patterns that crashes
  this runtime. A field that can only be submitted with one particular key is a
  field some people cannot use at all.
### Live preview, beside the source

A read-only pane next to the editor that renders the open note as Markdown
(`Ctrl+R`). It is a **projection, never the source**: saving writes the `TextBox`,
exactly as it always did, and the editor's crash-prone paths are untouched. The
decision not to *replace* the editor with a rendered view, and why the renderer is
hand-written in Core instead of a library, is
[0013-live-preview-and-source](decisions/0013-live-preview-and-source.md).

`MarkdownParser` (Core) is line-based: block first, then inline, with a closed set
of output shapes (`Block`/`Inline`) so it can be tested without any UI - which, given
this app's crash history, is the only way a component like this can be developed at
all. 60 tests, and the last group of them pins down what it does **not** do, so a
degradation nobody discovers is a limitation rather than a bug report.

Two rules that came out of writing it:

- **Unrendered syntax is never shown as punctuation.** `**bold**` renders as "bold"
  without the stars. A missing style is a limitation; a stray `**` is a bug.
- **Emphasis openers follow whitespace, closers do not.** The first version had the
  whitespace rule on the wrong side, so the extremely common `con **negritas**` did
  not render at all - and the "2 * 3 * 4" case it was meant to protect is actually
  handled by the *closing* rule. A test with a real sentence found it.

The preview refreshes on a 400ms debounce off the dispatcher, never while typing:
reading the text on every keystroke and rebuilding the visual tree from a
text-changed handler is the documented crash.
### The graph view, as an instrument

A force graph is only useful if you can move around in it, and the first version
had a mouse that did nothing: the wheel was ignored (only touch reached the
manipulation events), and a node could not be picked up at all. What it has now,
following Obsidian because it is the behaviour people already have in their hands:

- **Wheel zooms, anchored on the pointer**, so whatever is under the cursor stays
  under the cursor. Shift+wheel and Alt+wheel pan instead, and the pan is the only
  way to move a graph vertically on a machine with no scroll lock.
- **Drag a node to move it.** The node lifts, follows the pointer, and settles back
  when released; its edges follow it, because an edge that keeps pointing at where
  the node *was* is the single ugliest thing a force graph can do. A dropped node is
  **pinned**: the layout re-applies pins after every pass, so a hand-made
  arrangement survives a refresh. "Reset view" unpins everything, and says so on the
  button.
- **Pan by dragging the background**, or with the middle or right button.
- **Hovering a node lights it and its neighbours and fades the rest** - which is the
  reason to draw edges at all. Without a way to see which edges belong to a node,
  the lines are noise.
- **Click selects, double-click opens the note.** Obsidian opens on a single click;
  here a click selects, because the bar under the graph is a real panel (path, tags,
  "make this the centre") and losing it on every tap would be the worse trade.

Two decisions worth defending:

- **The wheel and the pointer handlers are on the page, not on the canvas.** Pointer
  events bubble, so a page-level handler sees the wheel wherever it lands inside the
  graph - over a node, over a label, over the background - instead of depending on
  which element happens to be the hit target at that pixel. On the canvas it silently
  did not fire at all. The gesture is then filtered to the canvas's own bounds so a
  wheel over the scope dropdown still scrolls the dropdown.
- **There is deliberately no fade-in on render.** There was one, and it set every
  node and label to `Opacity 0` and animated to 1 - and on this runtime the storyboard
  did not always run, leaving the labels invisible and the graph blank. A graph that
  renders nothing is the worst possible failure for this feature, and an entrance
  animation is worth nothing next to that. The lift and the settle animate a
  transform and always end on a valid value, so they stayed.

The hint under the graph lists every gesture. It is the only discoverability there
is for a canvas, and a gesture nobody can find is a feature nobody has.
### The graph, and two bugs that made it look empty

Found by looking at the screen, after someone said "I press Graph and nothing
happens". Both were in place and had been since Phase 7. Neither was ever visible
to the tests, which is the interesting part.

**The canvas was two pixels tall.** `GraphPage.xaml` put the canvas in an `Auto`
row and the *status line* in the star row. A `Canvas` reports no desired size from
its children, so an `Auto` row containing one collapses to its border - and the
star row, holding a single line of text, took the entire window. The graph was
built, laid out, positioned and sitting in the visual tree the whole time, in a
canvas the height of a hairline. The star row belongs to the canvas.

**The layout collapsed everything into the middle.** The force simulation ended
each iteration with

```csharp
xs[i] = (xs[i] + width / 2) / 2;   // "pull it back toward the middle"
```

which is not a gentle nudge applied once - it is a contraction applied 300 times.
The equilibrium of `offset_next = (offset + step) / 2` is about the *step size*,
not the spacing, so every node in the vault ended up within one temperature of the
centre no matter how many edges it had: nineteen nodes and thirty-six relationships
inside a blob about 120 pixels across. Containment is now a clamp, and the finished
layout is fitted to the viewport afterwards, which is what turns "the algorithm
converged" into "there is a graph on screen".

The Phase 7 tests passed throughout, because they asserted the wrong things:
"more than one pixel apart" and "all positions distinct". Both are true of a graph
collapsed to a point. They now assert a **fraction of the viewport** used, and that
the two closest nodes are further apart than a node is wide - distinct coordinates
are not the same as a legible graph. Worth remembering: a test that cannot fail for
the bug you are worried about is worse than no test, because it is a claim.
### Attachments (Phase 11)

**An attachment is identified by the hash of its contents.** The file is named after
it, in the `Attachments/` folder the vision already specifies. That one decision does
all the work: deduplication is free, "what have I not got" is a cursor over `seen_at`
and nothing else, renaming is a non-event, and — the part worth saying out loud —
**attachments cannot conflict**, because a hash has exactly one possible content.
There is no conflict model here and none is needed.

The bytes live in their own table, not in a `bytea` column on the metadata row:
"what have I not got" selects five columns and runs every few minutes, and making
that drag 32 MB blobs through memory on every device would be a slow way to learn a
file name.

What the server checks, and why:

- **The hash in the URL is verified against the body.** Believing the client would
  store corruption and hand it to every device that asked, and nobody would find out
  until an image failed to open weeks later. Mismatch is 422 and nothing is stored.
- **A size cap, enforced before the body is read and again while reading it**, because
  a shared token plus anonymous uploads plus no limit is how a Pi's SD card fills up
  at 3am.

The preview draws an image on a line of its own as a real `Image` control, decoded
asynchronously from a random-access stream (a decoded 4000x3000 PNG is ~48 MB; a byte
array would hold the file *and* the decode). Resolution is deliberately narrow — a
bare name is looked for in the attachments folder, a path is resolved inside the
vault, a URL is refused, and anything containing `..` is refused rather than
normalised. A missing image says so, because a silent gap is the failure people
describe as "it just does not show sometimes".

Three sync bugs found while building it, all of them the kind that only show up
against a real server with real state in it:

- **Every attachment was re-uploaded on every sync.** The push side had no way to ask
  "does the server already have this?", so it assumed not, and the client
  re-transferred the whole vault every five minutes. Fixed the way notes already do
  it: a per-attachment pushed mark, so a second sync pushes nothing. That is the
  difference between "syncs" and "uploads your vault, repeatedly".
- **The attachment watermark was the note cursor.** Two different streams with two
  different watermarks, sharing one means whichever ran ahead starves the other: a
  note synced at T+1h hides every attachment the server saw before T. Attachments
  have their own watermark now.
- **The paged pull repeated itself forever.** The comparison is a pair,
  `(seen_at, hash) > (cursorAt, cursorHash)`, and the server was being handed the
  *query's* watermark as `cursorAt` instead of the last row's - which is true for
  nearly every row, so page two was page one again. The second half of the cursor now
  travels as its own parameter rather than being parsed back out of a composite
  string, because a key that has to be split correctly will eventually be split
  wrongly.

And a fourth, smaller one: a download arrived with **no extension**, because a
content-addressed file has no name to carry one and I passed an empty string to keep
it. It is fixed from the server's declared content type, because a file called
`3f7868...` is present and useless - nothing will open it.

Two more things found while building it:

- **`![[diagram.png]]` was being read as a wiki link**, so every note with a picture
  in it reported an unresolved link — a status line that cries wolf on ordinary notes
  is a status line nobody reads. The embed marker is now skipped by
  `WikiLinkParser`.
- **.NET numbers unnamed regex groups before named ones.** In
  `(?<Bang>!?)\[\[([^\]|#]+)\]\]` the target is group 1 and `Bang` is group 2 — the
  opposite of the order they are written in. Reading the target positionally returned
  an empty list while looking perfectly correct. Both groups are named now, and the
  trap is written down next to the pattern.
## Seeing the UI from the shell

Screenshotting this app used to be impossible from here, which is why "it looks
fine" went unverified for nine phases. It is possible, and worth doing:

- `PrintWindow` with `PW_RENDERFULLCONTENT` (flag 2) captures the window without
  touching it. `CopyFromScreen` works too but only shows whatever is on top.
- `PrintWindow` with `PW_RENDERFULLCONTENT` (flag 2) captures without touching the
  window. Note that it is **partial on this window**: the right sidebar and the
  status bar are frequently missing from the capture while the middle renders
  perfectly. Do not conclude a panel is broken because a capture does not show it;
  check `BoundingRectangle` through UIAutomation, which reports the real layout.
- **`SetForegroundWindow` and `ShowWindow` are safe.** An earlier version of this
  file said they killed the process, and that was wrong: the crash was a
  hand-written `ControlTemplate` (above), and it was fixed by the ribbon
  redesign. The false claim mattered, because it stopped the app being brought to
  the front for testing - which in turn is why the mouse gestures in the graph went
  unverified for a day. Raising the window first, then sending real `SendInput`
  mouse and wheel events at it, is how the graph's zoom and drag were tested.
- Real input works: `SetCursorPos` plus `mouse_event` reaches the app, and
  UIAutomation's `InvokePattern` works on Buttons. What does **not** work is
  `PostMessage` for keystrokes - WinUI ignores posted key messages, so Enter in a
  text box cannot be synthesised from here.
## Known Issues

- **A hand-written `ControlTemplate` whose `ContentPresenter` carries the button's
  content crashes the process** (0xc000027b, `Microsoft.UI.Xaml.dll`) as soon as
  that content is a shape rather than text. Found by launching, twice: the first
  build rendered nothing at all, which looked like a styling bug, and "fixing" it
  by adding the presenter is what started killing the app. The icon buttons
  therefore keep the framework's own template and only override the setters.
  `SidebarRowStyle` has the same shape of template and works, because its content
  is text - which is exactly why this was worth chasing down rather than working
  around.
- **A `DoubleAnimation` pointed at `Background` crashes the process.** A
  `DoubleAnimation` can only animate a double, and `Background` is a `Brush`. It
  did not crash on demand: it went off whenever the pointer-over state fired, which
  includes **by itself** the moment the window appears under the cursor, so the app
  died a few seconds after launch with nobody touching it - and, before that, every
  time someone moved the mouse over a sidebar row. Which is why "clicking a note
  crashes" and "it crashes on startup" turned out to be the same bug. The hover is
  now a separate `Border` whose `Opacity` is animated, which is a real double.
- **Ribbon icon geometry: one `Path` per icon, no `Ellipse`, no line caps or joins.**
  Bisected by launch, not by reading. With `Ellipse` nodes inside nested `Grid`s and
  `StrokeStartLineCap`/`StrokeEndLineCap`/`StrokeLineJoin` attributes, the app died
  on roughly one launch in three with nothing happening; strip the shapes out and
  four consecutive launches were clean; put back a single `Path` per icon and five
  more were clean. Which of the two changes did it is **not established** - the
  bisect says "the icon shapes", not which part of them - so the rule here is the
  conservative one: simplest geometry that draws the shape, and if an icon needs a
  circle, use the framework's own drawing rather than a trick. A circle drawn as
  two identical arc commands is already known to be fatal; treat that as a symptom
  of the same area.
- **Opening a note must not rebuild the tree.** A row's `Click` runs while the button
  is still a child of its panel, so clearing that panel removes the control the
  event is being dispatched from. It is the same re-entrancy as the `Loaded` crash
  above, and it is a crash the user hits on the first thing they do. The selection
  highlight now swaps two styles on rows that already exist, folding a folder defers
  the rebuild to the next dispatcher cycle, and saving a note does not repaint the
  tree at all - writing to a note cannot create, move or delete one.
- **No `ToolTipService.SetToolTip` in code.** A tooltip is a popup, and popups are
  what this runtime kills the process over. Attaching one to a row while the pointer
  is on that row runs the popup machinery in the middle of a click. The static
  `ToolTipService.ToolTip` attributes in the XAML are fine - they are attached once
  at load, and the ribbon is unusable without them.

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

- **The same `STATUS_STOWED_EXCEPTION` came back in Phase 7, from
  mutating a control inside `Loaded`.** Auto-reopening the last vault
  (setting `VaultPathBox.Text` and rescanning) crashed the process
  natively on the first launch, again `0xc000027b` in
  `Microsoft.UI.Xaml.dll`. Deferring the whole thing to the next
  dispatcher cycle with `DispatcherQueue.TryEnqueue` - the same
  treatment `Editor.Text` already gets in `ShowAndEditNote` - fixes it,
  verified across repeated launches.
  **What this adds to the picture:** it isn't (only) about *reading*
  `TextBox.Text` back. Writing to a control's `Text` while the XAML
  tree is still processing `Loaded` is re-entrant, and the resulting
  XAML failure is stowed rather than thrown, which is why it can't be
  caught. The rule for this shell is therefore: **mutate controls from a
  deferred dispatcher callback, never inline in `Loaded`** - and treat
  any "fixed" native crash here as fixed only after several cold
  launches, not one.

- **Navigating the root `Frame` away from `MainPage` crashes the
  process**, and **opening any XAML popup crashes it too.** Both
  confirmed by bisection on this machine, both `0xc000027b`:
  - The Phase 7 graph view was originally reached with
    `RootFrame.Navigate(typeof(GraphPage))`. That *unloads* `MainPage`,
    and unloading a page holding the `Editor` TextBox killed the
    process - reproduced with an **empty** `GraphPage` and an empty
    `OnLoaded`, so it is the teardown, not the graph. The graph now
    lives in a `Frame` *inside* `MainPage` (`GraphHost`); `MainContent`
    is only hidden, so the editor is never torn down.
  - The Phase 8-ish note rename originally used a
    `MenuFlyout`/`ContextFlyout` on each note row, plus a
    `ContentDialog`. Right-clicking a note killed the process every
    time. Renaming is now **inline**: right-click swaps the row for a
    `TextBox` (Enter commits, Escape or losing focus cancels), which
    involves no popup at all. Verified: no crashes after the change.

  **The rules this adds for this shell:** don't navigate the root
  `Frame` away from the page that owns the editor, and don't use XAML
  popups (`MenuFlyout`, `ContextFlyout`, `ContentDialog`,
  `Flyout`) on this runtime at all. Native dialogs are fine - the
  `Microsoft.Windows.Storage.Pickers.FolderPicker` works, because it's
  a system dialog, not a XAML popup. If a future phase genuinely needs
  a popup, the first thing to try is a newer (non-preview) Windows App
  SDK, and verify with several cold launches before believing it.

- **Verifying UI behaviour from the tooling is awkward, and two
  things that look like findings aren't.** `TextBox.TextBox`'s UIA
  `InvokePattern` throws from a non-interactive shell (so a "click"
  scripted that way may never happen), and synthetic mouse input only
  lands while the app still has the foreground - which a background
  shell cannot reliably take. Meanwhile a `STATUS_STOWED_EXCEPTION`
  kill leaves a process that is *alive with a window but with an empty
  UIA tree*, which reads exactly like "the app did nothing". Both cost
  real time to rule out here; when the UI misbehaves, check the
  Application event log for `0xc000027b` before believing the UI is
  at fault.

- Phase 8 (server) verified live against a real PostgreSQL 16 in Docker:
  `/health` reports the resolved environment and database; a note PUT/GET
  round-trips; a write with an **older** `updated_at` is refused (the stored
  revision/hash stay put) while a newer one is applied; a path escaping the
  vault (`../escape.md`) is rejected with 400; DELETE returns 204 and leaves a
  tombstone that `/changes` reports with `deleted: true` and null content;
  DELETE without the device header is rejected with 400. The Production
  fail-fast was verified by accident first - `dotnet run` with no
  launchSettings defaults to Production, and the server refused to start,
  which is why `launchSettings.json` now exists.
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
- Phase 6 (tags/metadata) is covered by tests only so far — the tag panel and
  metadata line have not yet been exercised in a live shell run.
- Phase 7 (graph) is covered by tests only so far: the builder and the layout
  algorithm are unit-tested, but nothing has rendered a graph in a live shell
  run yet.
- Phase 9 (client sync) verified live end to end: a note pushed from a temp
  vault arrives on the server; a change made by a second device is pulled into
  the vault file; a tombstone from another device deletes the local file; a note
  changed on both sides is reported as **one** conflict with neither version
  overwritten; a wrong token fails without touching the vault; syncing twice
  sends nothing the second time. All 6 run as tests against the real stack.
- Phase 10 (conflicts/history) verified live against the real stack: writing a
  note twice leaves the first version readable; a deleted note's content is
  still in its history; a refused write leaves no version behind; "keep mine"
  pushes the local copy over the server's and clears the conflict; "take theirs"
  overwrites the local file and records the server's hash so the next sync
  doesn't re-conflict. A path collision is a 409, not a 500.
- The shell redesign verified live: launched, the vault reopened by itself, the
  tree, ribbon, status bar and both sidebars render, and the icons were checked
  by capturing the window (see "Seeing the UI from the shell").
- Automatic sync verified against the real stack, with the server up and down: it
  syncs once at startup without anyone asking, an automatic failure pauses it and
  says so, the pause is not repeated on every tick, a manual success lifts it, and
  creating a note arms the debounce. 222 tests pass (152 Core, 34 Data, 36 Server).
