# Synchronization

**Not implemented yet.** This is a placeholder describing the intended
philosophy so early schema/architecture decisions don't foreclose it. Full
design happens in Phase 9 (synchronization) and Phase 10
(conflict/version handling).

## Philosophy

- Local-first: writing a note never depends on the server or network.
  Sync is a background concern layered on top of a fully-functional
  offline app.
- Incremental: never re-upload the whole vault on every change. Each
  synchronized entity carries `id`, `device_id`, `revision`, `updated_at`,
  and `content_hash`, so the server can answer "give me everything changed
  since revision X."
- Asynchronous: typing must never wait on sync. Likely triggers (final
  choice deferred to Phase 9): debounce after edits, periodic sync, app
  startup/shutdown, a manual Sync action, network reconnection.

## Conflicts

Multiple devices may edit the same note independently. The server must
never silently overwrite one version with another — conflicting revisions
must be detectable. Resolution strategy (e.g. three-way merge, manual
pick, versioned fork) is designed in Phase 10; until then, the only
constraint on earlier phases is that revision tracking must remain
possible (see the open questions in [architecture.md](architecture.md)
around stable note identity and revision columns).
