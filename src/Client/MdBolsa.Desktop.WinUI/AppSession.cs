using MdBolsa.Data.Links;
using MdBolsa.Data.Search;
using MdBolsa.Data.Attachments;
using MdBolsa.Data.Sync;
using MdBolsa.Data.Tags;
using MdBolsa.Data.Vault;
using Windows.Storage;

namespace MdBolsa_Desktop_WinUI;

// The shell's shared per-session state. Phase 7 added a second page (the graph
// view) that needs the same vault and the same indexes MainPage uses, so the
// vault path and the index factories moved here. Deliberately a static class
// holding one vault: this is a single-window, single-vault app, and a DI
// container is well past what the current phases need (see
// docs/architecture.md's dependency table).
internal static class AppSession
{
    // ApplicationData.Current is the packaged app's own settings store, so this
    // lands in the app's virtualized LocalState and nowhere else - no path
    // juggling, and nothing for a script to seed from outside the app (see the
    // %LOCALAPPDATA% virtualization note in docs/architecture.md).
    private const string RememberedVaultKey = "MdBolsa.VaultPath";

    public static string? VaultPath { get; set; }

    // The notes page, so the graph view can hand control back ("Back to notes").
    // This used to travel as a Frame navigation parameter, but it never arrived -
    // and Window.Current is null in this app, so there was no fallback path. AppSession
    // already exists to share state between the two views; this is the same idea.
    public static MainPage? Host { get; set; }

    // The app window's id, captured by MainWindow on startup. Native dialogs (the
    // folder picker) need to know which window owns them, and Window.Current is null
    // in this app - so this is the one reliable way to get it. See
    // Microsoft.Windows.Storage.Pickers.FolderPicker's constructor.
    public static Microsoft.UI.WindowId? WindowId { get; set; }

    // The note MainPage last opened, so the graph view can default its
    // note-local graph to something the user is actually looking at.
    public static string? CurrentNoteRelativePath { get; set; }

    // The last vault the user chose, so a restart doesn't mean picking a folder
    // again. Remembering a path is not the same as opening it: the caller still
    // checks the folder exists before scanning.
    public static void RememberVaultPath(string path) =>
        ApplicationData.Current.LocalSettings.Values[RememberedVaultKey] = path;

    public static string? RecallVaultPath() =>
        ApplicationData.Current.LocalSettings.Values.TryGetValue(RememberedVaultKey, out var value)
            ? value as string
            : null;

    // Every index lives in the same SQLite file - it's a rebuildable cache of the
    // vault, not a set of separate stores. Packaged apps get %LOCALAPPDATA%
    // virtualized per package; see docs/architecture.md.
    public static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db");

    public static SqliteVaultIndex OpenVaultIndex() => new(DatabasePath);

    public static SqliteLinkIndex OpenLinkIndex() => new(DatabasePath);

    public static SqliteSearchIndex OpenSearchIndex() => new(DatabasePath);

    public static SqliteTagIndex OpenTagIndex() => new(DatabasePath);

    public static SqliteSyncStateStore OpenSyncStateStore() => new(DatabasePath);

    public static SqliteAttachmentIndex OpenAttachmentIndex() => new(DatabasePath);

    // --- Sync identity and connection ------------------------------------
    //
    // The device id is per *machine*, not per vault, so it lives in the app's
    // settings store rather than in the per-vault SQLite index. It's created once
    // and never rotated: it is how the server records who wrote last.

    private const string DeviceIdKey = "MdBolsa.DeviceId";
    private const string ServerUrlKey = "MdBolsa.ServerUrl";
    private const string ServerTokenKey = "MdBolsa.ServerToken";
    private const string PreviewWidthKey = "MdBolsa.PreviewWidth";

    public static Guid DeviceId
    {
        get
        {
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(DeviceIdKey, out var value) &&
                value is string stored && Guid.TryParse(stored, out var id))
            {
                return id;
            }

            var created = Guid.NewGuid();
            ApplicationData.Current.LocalSettings.Values[DeviceIdKey] = created.ToString();
            return created;
        }
    }

    public static string? ServerUrl
    {
        get => ReadSetting(ServerUrlKey);
        set => WriteSetting(ServerUrlKey, value);
    }

    public static string? ServerToken
    {
        get => ReadSetting(ServerTokenKey);
        set => WriteSetting(ServerTokenKey, value);
    }

    /// <summary>
    /// How wide the preview pane was left, in pixels. Null means "the default".
    /// A preference, not state: a window that forgets how you sized a pane on every
    /// launch is a window you have to set up again every morning.
    /// </summary>
    public static double? PreviewWidth
    {
        get
        {
            var stored = ReadSetting(PreviewWidthKey);
            return double.TryParse(stored, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var width)
                ? width
                : null;
        }
        set => WriteSetting(PreviewWidthKey, value?.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? ReadSetting(string key) =>
        ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out var value) ? value as string : null;

    private static void WriteSetting(string key, string? value)
    {
        if (value is null) ApplicationData.Current.LocalSettings.Values.Remove(key);
        else ApplicationData.Current.LocalSettings.Values[key] = value;
    }
}
