using MdBolsa.Contracts;

namespace MdBolsa.Core.Sync;

// Where the client remembers what it last synced. Implemented in MdBolsa.Data
// against the same SQLite file as the other local indexes: a cursor and a pushed
// hash are local derived state, exactly like a search index, and belong with the
// other indexes rather than in a second store.
//
// The device id is *not* here - it's per machine, not per vault, and the shell
// owns it (see AppSession).
public interface ISyncStateStore
{
    // The (updated_at, id) of the last change successfully applied. Null means
    // "never synced", which asks the server for everything.
    NoteCursor? GetCursor();

    void SetCursor(NoteCursor cursor);

    // Forgets the cursor, so the next sync asks for everything from the epoch
    // again. The recovery path for a cursor that has gone wrong (and how a client
    // recovers from a server whose history it can't line up with).
    void ResetCursor();

    // The attachment pull's own position, kept separately because a sync that moved
    // it and then failed would silently skip whatever was in between.
    string? GetAttachmentCursor();

    void SetAttachmentCursor(string? cursor);

    // The other half of the page cursor. The comparison the server does is a pair,
    // and passing only the hash makes it repeat the page it just sent.
    DateTimeOffset? GetAttachmentPageSeenAt();

    void SetAttachmentPageSeenAt(DateTimeOffset? seenAt);

    // The attachment *watermark* - the server's `seen_at` as of the last successful
    // pull. Deliberately not the note cursor: notes and attachments are two different
    // streams with two different watermarks, and sharing one means a note synced
    // ahead of an attachment silently drops that attachment forever.
    DateTimeOffset? GetAttachmentSince();

    void SetAttachmentSince(DateTimeOffset? since);

    // Whether the server is already known to have this attachment. Without it every
    // sync re-uploads every file in the vault, which is not slow on a home network
    // but is the difference between "syncs" and "transfers your whole vault, again".
    bool IsAttachmentPushed(string hash);

    void MarkAttachmentPushed(string hash);

    /// <summary>Forgets pushed marks, so the next push sends everything again.</summary>
    void ClearAttachmentPushed();

    // The content hash the server last confirmed for a note. Compared against the
    // note's current hash, it answers the only question that matters before
    // pushing or overwriting: "did I change this since the server last saw it?"
    string? GetPushedHash(Guid noteId);

    void SetPushedHash(Guid noteId, string hash);

    // Writes the server refused because a newer revision already exists.
    // Reported to the user; resolving them is Phase 10's job, and nothing here
    // silently discards either version.
    IReadOnlyList<SyncConflict> GetConflicts();

    void AddConflict(SyncConflict conflict);

    // Called once a person has decided what should happen to a conflicted note
    // (see ConflictResolver). Until then the note stays out of sync in both
    // directions, which is the safe direction to be wrong in.
    void RemoveConflict(Guid noteId);

    void ClearConflicts();
}

// A note that was changed in two places at once. Phase 9 detects and reports
// these; it does not resolve them.
public sealed record SyncConflict(
    Guid NoteId,
    string? RelativePath,
    int? LocalRevision,
    int? ServerRevision,
    Guid? ServerDeviceId,
    DateTimeOffset DetectedAt)
{
    public override string ToString() =>
        $"{RelativePath ?? NoteId.ToString()}: local revision {LocalRevision?.ToString() ?? "?"} " +
        $"vs server revision {ServerRevision?.ToString() ?? "?"}" +
        (ServerDeviceId is null ? string.Empty : $" from another device");
}
