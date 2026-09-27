# 0011-attachments

- **Status:** Accepted
- **Date:** 2026-09-27

## Context

Phase 11 of [the roadmap](../vision.md#17-phased-roadmap). Every other entity in this
app has an identity, and the identity is a UUID in the note's frontmatter. That works
because a `.md` file is text, and text can carry its own name.

An attachment cannot. `Attachments/architecture.png` has nowhere to put an `id:`. So
the first question of this phase is not "how do I insert an image" - it is **what is
an attachment's identity**, and everything else follows from the answer:

- What does the server store, and under what key?
- How does a client ask for "what have I not got yet" without inventing metadata?
- What happens when the same image is pasted into three notes?
- What happens when someone renames the file?

The obvious answer - a UUID, like notes - requires a sidecar file, a manifest, or a
database table mapping UUIDs to paths, and every one of those becomes something to
keep in sync, to migrate, and to lose.

## Decision

**An attachment is identified by the hash of its contents.**

The `sha256` of the bytes is its id. The file on disk is named after it, in the
`Attachments/` folder the vision already specifies:

```
vault/
  Raspberry Pi.md
  Attachments/
    3f786850e387550fdab836ed7e6dc881de23001b.png
```

Everything else follows from that one choice:

- **Deduplication is free.** The same screenshot pasted into three notes is one
  file, synced once, stored once. With UUID identities it would be three files that
  happen to be equal.
- **"What changed?" is "what hash have I not got?"** The incremental pull is the
  same cursor-over-time query notes use, and the answer needs no path, no mtime and
  no rename tracking.
- **Renaming is a non-event.** There is nothing to update anywhere, because the
  filename is not the identity - it is a cache of it.
- **The note references the file by name**, as Markdown always has
  (`![[3f78...png]]` or `![](Attachments/3f78...png)`). Content that references an
  attachment by a *human* name (`![[arquitectura.png]]`) is resolved by hash at
  scan time and rewritten to the hashed name on the next save - the same rule
  already used for wiki links in [0008](0008-tags-and-metadata.md), because the
  alternative is a link that breaks when someone tidies their filenames.

The hash is a filename, not a hidden database: a person who finds the vault in five
years can still read it, move it to Obsidian, or delete it, and nothing breaks.

### The server

Two new tables and four endpoints, mirroring notes as closely as binary allows:

- `attachments` - hash, size, content type, first-seen, and the `device_id` that
  uploaded it. **No path, no revision, no `updated_at` that can go backwards**:
  content-addressed data has no history to speak of, and pretending otherwise is how
  a sync protocol grows a conflict model it does not need. An attachment cannot
  conflict, because there is only ever one version of a given hash.
- `PUT /api/attachments/{hash}` - upload bytes. The hash in the URL is verified
  against the body; a mismatch is a 422 and nothing is stored. Trusting the client's
  claim would let a corrupt download propagate silently to every device.
- `GET /api/attachments/{hash}` - download.
- `GET /api/attachments/changes?since=&cursor=` - the incremental pull, the same
  `(seen_at, hash)` cursor as notes.

A size cap per file, enforced on the server, because "one shared token" plus
"anyone on the network can upload" plus "no limit" is how a Raspberry Pi's SD card
fills up at 3am.

### What is not in this phase

- **Attachment *reference* tracking** (which note uses which attachment, and what to
  say when one is deleted). It is a real feature and it needs its own answer; the
  index records the reverse direction only.
- **Deduplicating what is already on disk.** Two identical files in `Attachments/`
  are two files until something renames one. The *server* deduplicates; the vault
  does not, because renaming a file a person may have referenced by hand is a worse
  failure than a duplicate.
- **Thumbnails, resizing, EXIF stripping.** Read and serve the bytes as they are.
- **Attachment history.** There is none, by the argument above.

## Consequences

- The `Attachments/` folder is now part of the vault contract. Anything in it is
  treated as an attachment; a stray text file in there gets indexed and synced,
  which is harmless and reversible.
- Two devices that both paste the same image converge on one file, and the second
  one deletes its own copy when it sees the attachment is already present. That is
  the first genuinely convergent thing in the sync protocol, and it is worth saying
  out loud: **attachments cannot conflict, and that is a feature of identifying
  them by content.**
- An attachment that exists in a note but not in the vault resolves as broken, the
  same as a broken wiki link, and the preview says so rather than showing a gap.
- The token in this phase is still one shared token for everything, which is the
  Phase 9 decision and is still only safe on a home network. Uploads make that
  slightly worse than downloads did, because the server now accepts bytes from
  anyone holding the token. That is the argument for per-device tokens, and it is
  now the strongest one yet.
