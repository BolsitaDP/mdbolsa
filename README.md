# mdbolsa

A personal, local-first, self-hosted knowledge management application —
Markdown notes, wiki-links, backlinks, search, a knowledge graph, canvas,
and multi-device sync — inspired by Obsidian but with its own architecture.

Full requirements: [docs/vision.md](docs/vision.md). Architecture:
[docs/architecture.md](docs/architecture.md). Decision log:
[docs/decisions/](docs/decisions/).

## Status

**Search and a quick switcher.** Typing part of a note's *name* finds it, even when
the name never appears in the note itself - which is what FTS5, an index over note
text, structurally cannot do. Matches rank exact, then prefix, then word-start, then
anywhere, and the full-text index is the fallback rather than the first answer.
`Ctrl+P` opens the switcher, which shows what you have been reading when you have
nothing typed yet. The search field has a submit button as well as Enter, because
filtering as you type is one of the patterns that crashes this runtime and a field
only one key can submit is a field some people cannot use.

**Sync happens on its own now.** Opening a vault syncs once, edits made on another
device arrive every five minutes, and a burst of saves is pushed once after twenty
seconds of quiet - none of it on the save path, so typing never waits for the
network. If the server can't be reached, the background sync *pauses* and says so
rather than retrying every thirty seconds; pressing Sync is the way back.

**Note history is finally visible.** The server has been keeping every revision
since Phase 10 and the client could ask for them - but the UI never did, so you
could resolve a conflict and never go back. The right sidebar now lists the open
note's revisions, newest first, with each one's content a click away and a
**Restore** button on the ones that aren't current. Restoring is not undo: it makes
that revision the note's current content, and the next sync pushes it as a new
revision, so nothing is ever removed from the history.

**A new shell, shaped like Obsidian's.** The window is now a narrow icon ribbon,
the vault as a folder tree on the left, the note in the middle, the note's tags,
backlinks and metadata on the right, and a status bar along the bottom - with
`Ctrl+F` to search, `Ctrl+S` to save, `Ctrl+B` / `Ctrl+I` to fold the sidebars
away, and `Ctrl+G` for the graph. The point of copying a layout people already
know is that nothing here needs explaining. Colours come from the system theme, so
it is right in light and dark.

What is *not* changed: every note is still a plain `.md` file you own, and the
editor is still a plain-text editor (Obsidian renders Markdown; we don't yet -
that is a phase of its own).

See [docs/architecture.md](docs/architecture.md#the-shell-obsidian-shaped) for how
it's put together, and the Known Issues there before adding UI - this runtime is
unforgiving, and the redesign found two more ways to crash it.

**Phase 10 - Conflicts and version history.** When a note is changed in two
places, the app now shows you both and asks - no version is merged or discarded
behind your back. The server keeps **every** accepted revision, so the version
you didn't pick is still there. Press **Conflicts** in the shell to see what's
unresolved and choose: "Keep mine" pushes your copy over the server's (the old
one becomes history), "Take theirs" writes the server's copy over your file.
Nothing is ever merged automatically - a wrong automatic merge is worse than
none. See [docs/synchronization.md](docs/synchronization.md) and
[0012](docs/decisions/0012-conflict-resolution.md).

**Phase 9 - Client sync.** Press **Sync** and the vault goes both ways; writing
a note never touches the network, so typing never waits on sync. Configure it
once with **Sync settings...** (server URL and shared token).

**Phase 8 - Server foundation.** `MdBolsa.Server`: an ASP.NET Core API over
PostgreSQL. **No authentication** beyond one shared token - an explicit,
temporary decision, see [0010](docs/decisions/0010-server-foundation.md) - so it
binds to localhost only, and in Production it refuses to start without a token.
See [docs/deployment.md](docs/deployment.md).

Earlier phases: the WinUI shell opens a vault (Windows folder picker, choice
remembered), lists its notes, lets you open/edit/save one as plain Markdown text
while seeing its backlinks, searches the whole vault (SQLite FTS5 - no new
dependency), reads tags from frontmatter and inline `#hashtags` into a
filterable panel, shows a read-only metadata line, has a graph view
(whole-vault or note-local, force-directed layout computed in Core), and renames
notes on right-click. Phase 3's editor was blocked for a while by a WindowsAppSDK
2.4.0 crash reading a `TextBox`'s edited content back; that and two later native
crashes are documented in docs/architecture.md's Known Issues - read it before
adding UI, because this runtime is unforgiving. See
[docs/vision.md 17](docs/vision.md#17-phased-roadmap) for the full roadmap.
## Running it

Double-click **`mdbolsa.lnk`** on the desktop: it rebuilds and launches the
shell. There is no standalone `.exe` - this is a packaged WinUI 3 app, so it
needs package identity; see [docs/development.md](docs/development.md) for why,
and for the exact commands.

## Structure

```
docs/                       Vision, architecture, dev/deploy/sync docs, ADRs
src/Client/MdBolsa.Core/     Domain logic, parsing, indexing (platform-agnostic)
src/Client/MdBolsa.Data/     SQLite-backed storage (platform-agnostic)
src/Client/MdBolsa.Desktop.WinUI/  WinUI 3 shell
src/Shared/MdBolsa.Contracts/ Sync wire contract, shared by client and server
src/Server/MdBolsa.Server/   ASP.NET Core sync/storage API (Phase 8)
tests/                       Unit tests for Core, Data and Server
samples/dev-vault/           Fake vault used as the Development default
config/                      `.example` templates for local config/secrets
run.cmd                      Build + launch the desktop shell
docker-compose.yml           Development stack (Postgres)
docker-compose.prod.yml      Raspberry Pi stack (Postgres + API)
Dockerfile                   API image for the Pi
```

## Build

```bash
dotnet build src/MdBolsa.sln
dotnet test src/MdBolsa.sln
```

Running the WinUI 3 shell (not just building it) requires Windows Developer Mode
enabled and the `winapp` CLI (plain `dotnet run` doesn't register the debug
package identity correctly with this preview tooling). Once it's running, click
"Browse..." to pick a vault folder (the path is remembered for next time), then
click a note to view/edit it and see its backlinks, click a tag in the panel to
filter the list by it, right-click a note to rename it, type into the search box
and click "Search" ("Show all notes" clears back to the full list), or click
"Graph" for the knowledge-graph view. See
[docs/development.md](docs/development.md) for the full dev setup and the
branching workflow.

## Principles

- **Local-first.** The desktop app is fully usable offline; the Raspberry
  Pi is a sync/storage server, never the primary runtime.
- **Markdown is canonical.** SQLite and PostgreSQL only ever hold derived
  indexes and sync state, never the only copy of note content.
- **Dev/Prod isolation.** Development never touches production data;
  switching to Production is always an explicit, deliberate step - the
  server refuses to start in Production without a connection string.
- **No overengineering.** Every dependency and abstraction has to earn its
  place against the current phase's actual need.
