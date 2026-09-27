namespace MdBolsa.Core.Attachments;

/// <summary>One attachment found in the vault.</summary>
public sealed record AttachmentMetadata(
    string Hash,
    string RelativePath,
    long Size,
    string ContentType,
    DateTimeOffset IndexedAt);

public sealed record AttachmentScanResult(int Added, int Unchanged, int Missing);

/// <summary>
/// The hashing rule, in one place, because both the scanner and the sync client
/// have to agree with the server about it exactly.
///
/// SHA-256 of the file's bytes, lowercase hex. Identical bytes always produce an
/// identical name, which is the whole basis of this phase: the same screenshot
/// pasted into three notes is one file with one name on every device, and needs no
/// reconciliation because there is nothing that can disagree.
///
/// The file is named after its hash, not the other way round. The name is a cache
/// of the identity, so tidying filenames is harmless and re-hashing renames a file
/// that is not actually broken.
public static class AttachmentHash
{
    public static string Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool IsValid(string? hash) =>
        hash is { Length: 64 } && hash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// What counts as an attachment.
///
/// The vision puts them in an `Attachments/` folder at the vault root, and this is
/// that rule with the edges decided rather than assumed:
///
///   * Only inside the attachments folder. A stray `.DS_Store` next to a note, a
///     half-downloaded `.tmp`, or somebody's `desktop.ini` is not something to sync
///     to a server; a whole-vault "everything that is not Markdown" rule would.
///   * No dotfiles and no partial names, because those are almost always a tool in
///     the middle of doing something.
///   * The folder name is case-insensitive, since a vault synced from macOS and
///     Windows can disagree about it.
///   * The app's own index database is never in there, but if it somehow is, the
///     extension check keeps it out anyway - belt and braces for a file that would
///     otherwise be uploaded and never deleted.
public static class AttachmentRules
{
    public const string FolderName = "Attachments";

    public static string FolderPath => FolderName;

    public static bool IsInAttachmentsFolder(string relativePath) =>
        relativePath.Replace('\\', '/')
            .StartsWith(FolderName + "/", StringComparison.OrdinalIgnoreCase);

    public static bool IsAttachmentPath(string relativePath)
    {
        var normalised = relativePath.Replace('\\', '/');
        if (!IsInAttachmentsFolder(normalised)) return false;

        var name = normalised[(normalised.LastIndexOf('/') + 1)..];
        if (name.Length == 0 || name.StartsWith('.')) return false;
        if (name.EndsWith('~') || name.EndsWith(".tmp") || name.EndsWith(".crdownload")) return false;

        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1;
    }

    /// <summary>Everything in the attachments folder, oldest path order, so a scan
    /// is deterministic.</summary>
    public static IReadOnlyList<string> Enumerate(string vaultRoot)
    {
        var folder = Path.Combine(vaultRoot, FolderName);
        if (!Directory.Exists(folder)) return [];

        return Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(vaultRoot, path).Replace('\\', '/'))
            .Where(IsAttachmentPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    // By extension, for the handful worth naming. Everything else is
    // application/octet-stream, which is honest - a viewer sniffs the bytes anyway,
    // and a wrong guess in a Content-Type is worse than no guess.
    public static string ContentTypeFor(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    /// <summary>The extension a stored attachment gets, derived from its content type.
    ///
    /// Needed because a content-addressed file has no name to carry an extension: a
    /// download that arrives as "3f7868..." is present and useless, since nothing
    /// will open it. The server's declared type is the only evidence there is.</summary>
    public static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        "application/pdf" => ".pdf",
        "text/markdown" => ".md",
        "text/plain" => ".txt",
        "application/zip" => ".zip",
        _ => ".bin",
    };

    /// <summary>The file name an attachment should have, given its hash.</summary>
    public static string NameFor(string hash, string relativePath) =>
        hash + Path.GetExtension(relativePath).ToLowerInvariant();
}
