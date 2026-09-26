# 0012-conflict-resolution

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Phase 10 of [the roadmap](../vision.md#17-phased-roadmap): conflict and version
handling. Phase 9 could tell you *that* a note was changed in two places and
stopped there ([0011](decisions/0011-client-sync.md)). Two things were missing,
and they are different problems wearing the same name:

1. **History.** The server kept exactly one version per note, so "keep mine or
   take theirs" had nothing to show you. [architecture.md](../architecture.md#open-questions-flagged-for-later-phases-not-decided-now)
   flagged this back in Phase 2 as an open question: where does note history
   live?
2. **Resolution.** Even with both versions in hand, what should the app *do*?
   Merge them, pick one, or keep both?

## Decision

### The server keeps every accepted revision

A new `note_versions` table, written **in the same transaction as the write that
supersedes it**. That ordering is the point: archive-then-write means there is
never a moment where the old content is gone and the history row hasn't landed.
A refused write rolls the archive back with it, so a write that didn't happen
can't leave a version behind.

Nothing is pruned. A personal vault's edit history is small, and "keep the last
10" is a policy that quietly destroys the thing someone came looking for. If
that ever needs to change, it changes in one place.

Deletes archive too, so an accidental deletion is still recoverable.

The API grows two reads and no writes:

```
GET /api/notes/{id}/versions        the history, newest first
GET /api/notes/{id}/versions/{rev}   one revision's content
```

### Resolution is a choice between two versions, and nothing merges

`ConflictResolver` offers exactly two operations:

- **Keep mine** — push the local content with a fresh timestamp. It needs no
  special endpoint: the server's rule is that a newer write wins, so *that is*
  taking over, and the version it replaces is already archived. The revision
  number is set above anything seen so the history reads sensibly.
- **Take theirs** — fetch the server's copy and write it over the local file. No
  write to the server at all: it already has this content, and the whole point
  is recording its hash so the next sync doesn't see the freshly written file as
  a new local edit and conflict all over again.

**There is no merge, and that is the decision worth arguing for.** A real merge
needs a common ancestor (which we now *have* - the previous revision) and
Markdown-aware diffing, because two edits to the same paragraph are not two edits
to a line. Getting that wrong produces a third version that neither person
wrote, presented as if it were the resolution. A wrong automatic merge is worse
than no merge: it destroys the original disagreement quietly, and the person who
has to notice is the one who cared. So: show both, let a human choose, keep the
loser in the history. Automatic merging is a possible later phase with its own
ADR, and it should be asked for by someone who has hit a real merge.

Until a conflict is resolved the note stays out of sync **in both directions**,
which is the safe direction to be wrong in.

### A path collision is a conflict, not a server error

Phase 9 introduced `NotePathConflictException` and then failed to map it, so two
notes claiming the same vault path came back as a **500**. It is now a 409 with
the same body shape as a stale write, because it is the same kind of event: two
devices disagreeing about one note, which the client reports rather than crashes
on. Found by the end-to-end tests, not by reading the code.

### A cursor can be reset

`ISyncStateStore.ResetCursor()` forgets the sync watermark, so the next sync asks
for everything from the epoch again. It exists because a cursor that has drifted
- a server whose history no longer lines up, a restored database - is otherwise
unrecoverable without deleting the index by hand.

## Consequences

- Resolution needs a human. A vault edited from two devices at once will keep
  asking until someone answers, and the answer is one button.
- Nothing is lost by choosing wrong: the version you didn't pick is in
  `note_versions`, readable through the API. That is the promise this phase makes,
  and the end-to-end tests check it directly.
- History grows without bound. Fine for a personal vault; a busy shared server
  would want a retention policy, and this is the place to add one.
- `note_versions` duplicates the current content of every note, so the database
  is roughly twice the size of the vault's text. For a personal knowledge base
  that is nothing, and it is the price of being able to go back.
- Unresolved conflicts are shown in the shell, inline (no dialog - see the Known
  Issues in [architecture.md](../architecture.md#known-issues)) with the two
  buttons per note.
- The end-to-end tests share one development database, so they had to learn to
  isolate themselves: a sync from the epoch brings in every other test's notes,
  which turns "one note conflicts" into "five notes conflict". Each test now
  settles its own note and then throws the foreign ones back out, and cleans up
  its rows by id *and* by path prefix, because a pulled note re-pushed by a client
  gets an id the test never saw.
