# Project Vision & Requirements (Source of Truth)

This document preserves the original project brief, verbatim in intent, as
given at project kickoff (2026-09-15). It is the source of truth for *what
we want at the end* — every architectural decision and phase should trace
back to something here. If a future decision seems to contradict this
document, update this document deliberately (with a note on why), rather
than silently drifting from it.

> Project: Local-First Self-Hosted Knowledge Management Application

## 1. Core Product Vision

A personal knowledge management system centered around Markdown notes,
inspired by applications such as Obsidian, but designed around a
local-first and self-hosted architecture. It should eventually support:

- Markdown notes
- Folders
- Tags
- `[[Wiki Links]]`
- Backlinks
- Note relationships
- Global search
- Local graph view
- Global knowledge graph
- Visual diagrams
- Mermaid diagrams
- Canvas / infinite board
- Attachments
- Images
- Tables
- Note metadata
- Note history
- Multiple devices
- Synchronization
- Offline usage
- Conflict detection
- Backups

The UX can take inspiration from Obsidian, but this is **not** intended to
be a direct code or UI clone. The application should have its own
architecture and implementation.

## 2. Fundamental Principle: Local First

The desktop application must remain fully usable without the Raspberry Pi
or an internet/network connection. Writing a note must **never** depend on
the server. The general model is:

```
User edits note
  -> Immediately saved locally
  -> Local indexes updated
  -> Application continues working normally
  -> Background synchronization detects changes
  -> Changes are eventually synchronized with Raspberry Pi
```

The Raspberry Pi is **not** the primary runtime for the editor. It acts
primarily as:

- Synchronization server
- Central storage
- Attachment storage
- Revision/history storage
- Backup location
- Device coordination service

## 3. Desktop Application

The first desktop target is Windows. Performance and resource usage are
major priorities. Preferred stack:

- C#
- .NET
- WinUI 3
- SQLite

Avoid Electron. Do not embed an entire browser application simply to
implement the desktop UI. WebView2 may eventually be used selectively if a
specific visualization genuinely benefits from web technology, but it
should not become the foundation of the application.

The application should:

- Start quickly
- Consume little RAM while idle
- Consume very little CPU while idle
- Behave like a native Windows application
- Integrate properly with Windows
- Still allow a modern, polished UI

Future macOS/Linux clients may exist, but they must **not** compromise the
quality of the Windows implementation right now.

## 4. Local Data Architecture

Markdown files remain the canonical, user-readable representation of notes
whenever practical. Example:

```
Vault/
├── Personal/
│   ├── Home.md
│   └── Finances.md
│
├── Development/
│   ├── Docker.md
│   ├── Raspberry Pi.md
│   └── CSharp.md
│
└── Attachments/
    ├── architecture.png
    └── example.pdf
```

The user must retain ownership of their data. If this application
disappeared tomorrow, the Markdown files and attachments should still be
accessible.

SQLite is used for local application state and indexes such as:

- Notes
- Note metadata
- Links
- Backlinks
- Tags
- Attachment references
- Search indexes
- Revisions if appropriate
- Synchronization state
- Application state

The local SQLite database must **never** be the only place where note
content exists.

## 5. Client-Side Processing

The Raspberry Pi should not perform expensive UI-related processing. The
desktop client eventually calculates/renders:

- Markdown
- Search indexes where appropriate
- Backlinks
- Graph layouts
- Local graph
- Global graph
- Diagrams
- Canvas
- Visual relationships
- Filters
- Graph animations
- Graph clustering

The Raspberry Pi primarily stores and synchronizes data.

## 6. Raspberry Pi Server

The production synchronization server runs on a Raspberry Pi 5. Eventually:

- Docker / Docker Compose
- ASP.NET Core API
- PostgreSQL
- Filesystem storage for attachments/data
- Backup service

For now, the Raspberry Pi uses a microSD card. In the future, persistent
application data migrates to a SATA SSD. Persistence must be designed so
this migration does **not** require redesigning or rebuilding the
application. Persistent paths/volumes must be configurable — conceptually
similar to an `APP_DATA_PATH=/some/path` setting, changeable without
application code changes.

## 7. Development vs Production

**This requirement is extremely important.** Development must be
completely isolated from the Raspberry Pi production environment. The
Windows development machine has its own development API, development
database, development storage, development configuration, and test
vaults. Development changes must **never** modify production notes or
production databases.

```
Windows PC (DEV)                    Raspberry Pi (PRODUCTION)
├── Desktop application             ├── Production API
├── Local API                       ├── Production database
├── Local database                  ├── Production storage
└── Test data                       └── Real personal data
```

Running the application from Visual Studio or the command line during
development must default to DEV. Connecting a development build to
production requires an explicit configuration change and should eventually
have additional safeguards. Never hardcode production credentials, IP
addresses, paths, or secrets.

## 8. Git Workflow

Branches: `main`, `develop`, `feature/*`.

- **`main`** — stable code, production-ready. Raspberry Pi deployments come
  from tagged/stable versions derived from `main`.
- **`develop`** — integration branch; completed features merge here first.
- **`feature/*`** — individual development tasks (e.g.
  `feature/basic-editor`, `feature/wiki-links`, `feature/backlinks`,
  `feature/search`, `feature/sync-engine`, `feature/graph-view`).

Normal workflow: `feature/*` → `develop` → testing → `main` → version tag →
explicit Raspberry Pi deployment. Production must **not** auto-deploy every
commit; deployments are intentional.

## 9. Configuration

Environment-specific configuration (e.g. `appsettings.Development.json`,
`appsettings.Production.json`). Secrets are never committed. `.env.example`
or equivalent documents any required environment variables. Production
configuration eventually includes database connection, storage path,
server URL, authentication secrets, and backup paths.

## 10. Synchronization Philosophy

Not implemented yet unless explicitly requested in a later phase, but the
architecture must not make it unnecessarily difficult later. Synchronization
is eventually incremental — never re-upload the whole vault on every
change. Each synchronized entity eventually has identifiers/metadata
conceptually similar to `id`, `device_id`, `revision`, `updated_at`,
`content_hash`. The server should eventually be able to answer "give me
everything changed since revision X." Synchronization happens
asynchronously — typing must never wait for it. Possible triggers later:
debounce after modifications, periodic sync, app startup/shutdown, manual
Sync button, network reconnection.

## 11. Conflict Handling

Eventually multiple devices may edit the same note (e.g. PC edits revision
20, phone independently edits revision 20, both sync). The server must not
silently overwrite one version. Conflict resolution is implemented later,
but architectural decisions must not make revision tracking impossible.

## 12. Knowledge Graph

A major long-term feature: notes and other entities become nodes,
relationships become edges. Layout and rendering happen on the client; the
server never calculates visual node positions. Eventually: global graph,
note-local graph, filtering, tags, groups, zoom, pan, node selection,
relationship exploration. Not implemented during the initial infrastructure
phase.

## 13. Visual Representations

Long term, the same underlying knowledge should support different
representations — Markdown, Graph, Canvas, Table, Timeline — without
unnecessarily duplicating the underlying information just because it has
multiple visual representations.

## 14. Development Philosophy

Incremental development. Do not overengineer early phases or introduce
infrastructure "because it may theoretically be useful someday." Before
adding a dependency, consider: why is it needed, can the platform already
provide this, what runtime cost does it introduce, what maintenance burden
does it introduce. Performance, idle resource usage, startup time, data
safety, and maintainability all matter.

## 15. Documentation

Maintain, at minimum: `README.md`, `docs/architecture.md`,
`docs/development.md`, `docs/deployment.md`, `docs/synchronization.md`,
and lightweight ADRs under `docs/decisions/` (e.g.
`0001-use-winui3.md`).

## 16. Testing

Introduce tests alongside meaningful logic. Prioritize tests for parsing,
storage, indexing, links, synchronization, conflict detection, and
migrations. Avoid tests that merely verify trivial UI implementation
details.

## 17. Phased Roadmap

Phases may be adjusted only for a strong technical reason:

0. Repository and architecture
1. Native Windows shell
2. Local Markdown vault
3. Editor
4. Wiki links and backlinks
5. Search/indexing
6. Tags and metadata
7. Local graph
8. Raspberry Pi server foundation
9. Synchronization
10. Conflict/version handling
11. Attachments
12. Canvas and diagrams
13. Mobile/other clients
14. Backups and hardening

Each phase should only implement what belongs to that phase — no jumping
ahead, no building the full application at once.
