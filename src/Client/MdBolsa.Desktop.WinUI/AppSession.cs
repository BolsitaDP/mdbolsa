using MdBolsa.Data.Links;
using MdBolsa.Data.Search;
using MdBolsa.Data.Tags;
using MdBolsa.Data.Vault;

namespace MdBolsa_Desktop_WinUI;

// The shell's shared per-session state. Phase 7 added a second page (the graph
// view) that needs the same vault and the same indexes MainPage uses, so the
// vault path and the index factories moved here. Deliberately a static class
// holding one vault: this is a single-window, single-vault app, and a DI
// container is well past what the current phases need (see
// docs/architecture.md's dependency table).
internal static class AppSession
{
    public static string? VaultPath { get; set; }

    // The note MainPage last opened, so the graph view can default its
    // note-local graph to something the user is actually looking at.
    public static string? CurrentNoteRelativePath { get; set; }

    // Every index lives in the same SQLite file - it's a rebuildable cache of the
    // vault, not a set of separate stores. Packaged apps get %LOCALAPPDATA%
    // virtualized per package; see docs/architecture.md.
    public static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db");

    public static SqliteVaultIndex OpenVaultIndex() => new(DatabasePath);

    public static SqliteLinkIndex OpenLinkIndex() => new(DatabasePath);

    public static SqliteSearchIndex OpenSearchIndex() => new(DatabasePath);

    public static SqliteTagIndex OpenTagIndex() => new(DatabasePath);
}
