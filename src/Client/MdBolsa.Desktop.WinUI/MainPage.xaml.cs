using MdBolsa.Contracts;
using MdBolsa.Core.Attachments;
using MdBolsa.Core.Links;
using MdBolsa.Core.Markdown;
using Windows.Storage;
using Windows.Storage.Streams;
using MdBolsa.Core.Search;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using ShapeRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.System;
using PathShape = Microsoft.UI.Xaml.Shapes.Path;

namespace MdBolsa_Desktop_WinUI;

// The shell: an Obsidian-shaped window around a folder of Markdown files. Open a
// vault, see it as a folder tree, open a note, edit it as plain text, and see
// what else in the vault points at it.
//
// The layout is a deliberate imitation: a narrow icon ribbon, the file tree on
// the left, the note in the middle, the note's context on the right, a status bar
// along the bottom, and Ctrl+F / Ctrl+S / Ctrl+B / Ctrl+I. None of that is
// original, and that is the point - a tool people use all day should be one they
// already know how to drive. What is ours is the tree, the backlinks, the tags
// and the sync, and the fact that every byte of it is a .md file they own.
//
// What is deliberately *not* here: any XAML popup. MenuFlyout, ContextFlyout and
// ContentDialog all crash this build of the Windows App Runtime with a stowed
// native exception, so right-click renames inline, sync settings are an inline
// panel, and the sidebar swaps views instead of opening menus. See
// docs/architecture.md's Known Issues before adding UI.
//
// The editor is plain text, not a rendered Markdown view. Obsidian renders; we
// don't yet (that's a phase of its own), so the middle of the window is a
// monospace text box - which is at least honest about what you're editing.
//
// One class, several files. It is split by what each piece does so that no single
// file is the whole window, but it stays one class on purpose: the pieces share the
// same controls and state, and turning them into separate classes would mean
// re-plumbing a UI that crashes on re-entrancy (see the Known Issues above). The
// split moved code and changed none of it.
//
//   MainPage.xaml.cs     state, the constructor, opening a vault, shortcuts, the
//                        graph host, scanning, and opening/saving a note
//   MainPage.Tree.cs     the sidebar's folder tree and inline rename
//   MainPage.Search.cs   search, tags and the quick switcher
//   MainPage.Sync.cs     automatic and manual sync, sync settings, device tokens
//   MainPage.Preview.cs  the live preview and its resizable divider
//   MainPage.History.cs  note history and conflict resolution
public sealed partial class MainPage : Page
{
    private string? _currentRelativePath;
    private string? _activeTag;
    private string? _activeQuery;
    private Dictionary<Guid, NoteMetadata> _notesById = new();

    // Folders the user has folded shut, by full folder path. In memory for the
    // session: persisting it is a nicety, and a wrong remembered state is worse
    // than an open folder.
    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);

    // The note rows currently in the tree, so opening a note can move the
    // selection without rebuilding anything. See HighlightOpenNote.
    private readonly Dictionary<string, Button> _noteRows = new(StringComparer.Ordinal);

    // Ctrl+P. The switcher is the same sidebar view as search, in a different
    // mood: a query is a *name* you half-remember, not words you remember from
    // inside a note, so name matches come first and the text index is the fallback.
    // With an empty query it shows what you have been reading, because that is what
    // a switcher opened with no intention in mind is usually for.
    private bool _switcherMode;

    // Most recently opened first. In memory for the session on purpose: this is a
    // convenience, and a remembered list that is wrong or stale is worse than no
    // list. The vault's own files remain the only source of truth.
    private readonly List<string> _recentlyOpened = [];

    // What the flat list is currently showing, so Enter can open the first one.
    private IReadOnlyList<NoteMetadata> _flatResults = [];

    public MainPage()
    {
        InitializeComponent();
        AppSession.Host = this; // so GraphPage's "Back to notes" can hand control back
        Loaded += OnLoaded;

        _syncTicker = DispatcherQueue.CreateTimer();
        _syncTicker.Interval = SyncTick;
        _syncTicker.Tick += OnSyncTick;

        // One-shot, restarted on every write: that is the whole debounce.
        _syncDebounce = DispatcherQueue.CreateTimer();
        _syncDebounce.Interval = SyncDebounce;
        _syncDebounce.IsRepeating = false;
        _syncDebounce.Tick += OnSyncDebounce;

        // The ticker runs for the life of the window. It costs a few comparisons
        // every 30 seconds and, when there is no server configured, decides to do
        // nothing at all - so it needs no separate on/off.
        _syncTicker.Start();

        // One-shot and rearmed, like the sync debounce: the preview updates a beat
        // after you stop typing, never while you are.
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = PreviewDelay;
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview(ReadCurrentNoteContent() ?? string.Empty);
        };
    }

    // Reopens the last vault on startup, so the common case is zero clicks. Guarded
    // on VaultPath being null, because Loaded also fires when coming back from the
    // graph view - the vault is still open then, and rescanning it again would
    // throw away the tag filter for no reason.
    //
    // The whole thing is deferred to the next dispatcher cycle, and that is not
    // cosmetic: mutating controls while the tree is still handling Loaded is
    // re-entrant and reliably reproduced the native STATUS_STOWED_EXCEPTION crash
    // documented in docs/architecture.md's Known Issues (0xc000027b in
    // Microsoft.UI.Xaml.dll). Same reason ShowAndEditNote defers Editor.Text.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is not null) return;

        var remembered = AppSession.RecallVaultPath();
        if (string.IsNullOrWhiteSpace(remembered)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (Directory.Exists(remembered))
            {
                OpenVault(remembered);
            }
            else
            {
                StatusText.Text = $"Last vault is not there anymore: {remembered}. Use the folder button to pick another.";
            }
        });
    }

    // --- Vault --------------------------------------------------------------

    // Windows' own folder picker, rather than making the user find and paste a path.
    //
    // This is Microsoft.Windows.Storage.Pickers.FolderPicker, not the UWP
    // Windows.Storage.Pickers one: the desktop picker takes the window id in its
    // constructor, while the UWP picker has no way to be told which window owns the
    // dialog and simply never shows one (verified: it hangs on PickSingleFolderAsync
    // with no dialog and no exception, which is why the earlier status-bar error was
    // all we ever saw).
    private async void OnBrowseVaultClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var windowId = AppSession.WindowId
                ?? throw new InvalidOperationException("The app window isn't ready yet.");

            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                CommitButtonText = "Open vault",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return; // cancelled

            OpenVault(folder.Path);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not open the folder picker: {ex.Message}";
        }
    }

    private void OpenVault(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "That folder isn't there. Use the folder button to pick your vault.";
            return;
        }

        SaveCurrentNote();
        _currentRelativePath = null;
        _activeTag = null;
        _activeQuery = null;
        SearchBox.Text = string.Empty;

        AppSession.VaultPath = path;
        AppSession.RememberVaultPath(path);
        VaultNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        ShowTree();
        RescanAndRefreshList();
        SyncOnceOnStartup();
    }

    // A local-first app does not own the folder: someone can drop a file in, or
    // sync something in, while the app is open. Rescanning is cheap and this is
    // the button that makes that recoverable without restarting.
    private void OnRescanClicked(object sender, RoutedEventArgs e) => RescanAndRefreshList();

    private void OnNewNoteClicked(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        var fileName = "Untitled.md";
        var counter = 1;
        while (File.Exists(Path.Combine(AppSession.VaultPath, fileName)))
        {
            counter++;
            fileName = $"Untitled {counter}.md";
        }

        File.WriteAllText(Path.Combine(AppSession.VaultPath, fileName), "# Untitled\n");
        RescanAndRefreshList();
        ShowAndEditNote(fileName);

        // Creating a note is a write like any other. It used to go unsynced until
        // the next manual sync, because the debounce was only armed from
        // SaveCurrentNote - which is a no-op when no note was open, which is
        // exactly the case when you make the first note of a session.
        ScheduleSyncAfterWrite();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveCurrentNote();

    // --- Keyboard shortcuts -------------------------------------------------

    // The shortcuts are KeyboardAccelerators declared in the XAML rather than a
    // KeyDown handler, for two reasons: the framework does the modifier test (and
    // works out which physical Ctrl was pressed), and an accelerator fires for a
    // focused control *and* still routes, so Ctrl+S works with the cursor in the
    // editor, which is where you will be when you want to save.
    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        SaveCurrentNote();

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _switcherMode = false;
        FocusSearch();
    }

    // Ctrl+P is Obsidian's quick switcher, and now there is one: the sidebar's
    // search view in a different mood - name matches first, and the notes you have
    // been reading when you have nothing typed yet.
    private void OnQuickSwitcherAccelerator(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        ShowQuickSwitcher();

    private void OnToggleLeftAccelerator(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        ToggleLeftSidebar();

    private void OnToggleRightAccelerator(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        ToggleRightSidebar();

    private void OnGraphAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        ShowGraph();

    private void FocusSearch()
    {
        if (LeftSidebar.Visibility != Visibility.Visible) ToggleLeftSidebar();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }
    // A right-pointing triangle, built in code because a Path's Data is a Geometry
    // and there is no string converter to lean on outside XAML. The chevron is
    // rotated rather than swapped between two shapes so the rotation is smooth.
    private static Geometry ChevronGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(3, 2),
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new LineSegment { Point = new Point(9, 6) });
        figure.Segments.Add(new LineSegment { Point = new Point(3, 10) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // --- Graph (Phase 7) ----------------------------------------------------

    // The graph lives in a Frame inside the centre column, *not* reached by
    // navigating the root Frame: that unloads this page, and unloading a page
    // holding the Editor TextBox crashes the process natively (STATUS_STOWED_EXCEPTION,
    // 0xc000027b) - verified by bisection, with an empty GraphPage and an empty
    // OnLoaded, so it is the teardown and not the graph. MainContent is only
    // hidden, so the editor is never torn down. See docs/architecture.md.
    private void OnGraphRibbonClicked(object sender, RoutedEventArgs e) => ShowGraph();

    private void ShowGraph()
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        SaveCurrentNote();
        AppSession.CurrentNoteRelativePath = _currentRelativePath;

        MainContent.Visibility = Visibility.Collapsed;
        GraphHost.Visibility = Visibility.Visible;
        GraphHost.Navigate(typeof(GraphPage));
    }

    // Called by the graph view when a note is double-clicked there. The graph lives
    // in a Frame in this column and is only ever hidden, never unloaded, so opening a
    // note from it is a hide plus a normal open - the same path the tree takes.
    public void OpenNoteFromGraph(string relativePath)
    {
        HideGraph();
        ShowAndEditNote(relativePath);
    }

    // Called by GraphPage's "Back to notes" button.
    public void HideGraph()
    {
        GraphHost.Visibility = Visibility.Collapsed;
        MainContent.Visibility = Visibility.Visible;
        GraphHost.BackStack.Clear();
    }

    // --- Scanning and the list ---------------------------------------------

    private void RescanAndRefreshList()
    {
        var vaultIndex = AppSession.OpenVaultIndex();
        var vaultResult = new VaultScanner(AppSession.VaultPath!, vaultIndex).Scan();

        var linkIndex = AppSession.OpenLinkIndex();
        var linkResult = new LinkScanner(AppSession.VaultPath!, vaultIndex, linkIndex).Scan();

        new SearchScanner(AppSession.VaultPath!, vaultIndex, AppSession.OpenSearchIndex()).Scan();

        var tagResult = new TagScanner(AppSession.VaultPath!, vaultIndex, AppSession.OpenTagIndex()).Scan();

        // Attachments are scanned here, with everything else, because a sync can only
        // push what the index knows about. Left out, the folder was never looked at
        // and nothing was ever uploaded.
        var attachmentResult = new AttachmentScanner(
            AppSession.VaultPath!, AppSession.OpenAttachmentIndex()).Scan();

        var notes = vaultIndex.GetAll().OrderBy(n => n.RelativePath).ToList();
        _notesById = notes.ToDictionary(n => n.Id);

        PopulateTagsPanel();

        // Short on purpose. This line is always on screen, so it earns its place by
        // being scannable, not by being complete: how many notes, and whether
        // anything needs attention. The per-scanner counts are development
        // instrumentation and belong in a log, not in someone's peripheral vision.
        var attention = linkResult.Unresolved > 0
            ? $", {linkResult.Unresolved} unresolved link(s)"
            : string.Empty;

        RefreshView(
            $"Scanned {_notesById.Count} note(s) · {vaultResult.Added} new, " +
            $"{vaultResult.Updated} changed{attention} · " +
            $"{tagResult.DistinctTags} tag(s) · " +
            $"{attachmentResult.Added} attachment(s) added.");
    }

    // Repaints whichever sidebar view is active, and the status line. One place,
    // because a rescan has to refresh the tree, the tags and the status, and
    // forgetting one of them is how a panel ends up lying about the vault.
    private void RefreshView(string? statusPrefix = null)
    {
        var count = _notesById.Count;

        if (_activeQuery is not null)
        {
            var results = AppSession.OpenSearchIndex().Search(_activeQuery);
            var notes = results
                .Select(result => _notesById.GetValueOrDefault(result.NoteId))
                .Where(note => note is not null)
                .Select(note => note!)
                .ToList();

            PopulateFlatList(notes, note => SnippetFor(note, _activeQuery), $"SEARCH  \"{_activeQuery}\"  ·  {notes.Count} match(es)");
            ShowSidebarView(FlatListPanel);
            StatusText.Text = statusPrefix is null
                ? $"{notes.Count} result(s) for \"{_activeQuery}\"."
                : $"{statusPrefix} {notes.Count} result(s).";
        }
        else if (_activeTag is not null)
        {
            var tagged = AppSession.OpenTagIndex().GetNoteIdsForTag(_activeTag).ToHashSet();
            var notes = _notesById.Values
                .Where(note => tagged.Contains(note.Id))
                .OrderBy(note => note.RelativePath)
                .ToList();

            PopulateFlatList(notes, secondary: null, header: $"#{_activeTag}  ·  {notes.Count} note(s)");
            ShowSidebarView(FlatListPanel);
            StatusText.Text = statusPrefix is null
                ? $"Filtering by #{_activeTag}: {notes.Count} note(s)."
                : $"{statusPrefix} Filtering by #{_activeTag}.";
        }
        else
        {
            RenderTree();
            ShowSidebarView(TreeList);
            StatusText.Text = statusPrefix is null ? $"Showing all {count} note(s)." : statusPrefix;
        }

        StatusDetailText.Text = $"{count} notes";
    }

    // Right-click starts a rename on any note entry - the tree, search results and
    // the backlinks panel.
    //
    // The editing UI is an inline TextBox swapped into the row, deliberately *not* a
    // MenuFlyout/ContextFlyout popup: opening a XAML popup crashes this build of the
    // Windows App Runtime (2.4.0 preview) with the same stowed native exception as
    // the rest of the Known Issues in docs/architecture.md - verified, it kills the
    // process on right-click. No popup, no crash.
    private static void AddNoteContextMenu(Button button, string relativePath)
    {
        button.RightTapped += (_, args) =>
        {
            args.Handled = true;
            AppSession.Host?.BeginInlineRename(button, relativePath);
        };
    }

    // --- Opening and saving a note -----------------------------------------

    private void ShowAndEditNote(string? relativePath)
    {
        SaveCurrentNote();
        _currentRelativePath = relativePath;

        // The file can vanish between being listed and being opened (external
        // delete, rename by another tool) - a real possibility for a local-first app
        // that doesn't own exclusive access to the vault.
        string content;
        try
        {
            content = relativePath is null ? string.Empty : File.ReadAllText(ResolvePath(relativePath));
        }
        catch (IOException)
        {
            EditorStatusText.Text = $"Could not open {relativePath} - it may have been moved or deleted.";
            _currentRelativePath = null;
            return;
        }

        AppSession.CurrentNoteRelativePath = relativePath;
        if (relativePath is not null) RememberOpened(relativePath);

        EditorStatusText.Text = relativePath is null ? "Select a note to edit." : $"Editing {relativePath}";
        NoteTitleText.Text = relativePath is null
            ? "No note selected"
            : Path.GetFileNameWithoutExtension(relativePath);

        // Deferred to the next dispatcher cycle - setting Editor.Text synchronously
        // inline in the click/scan call stack was part of what reproduced the crash
        // described above.
        DispatcherQueue.TryEnqueue(() => Editor.Text = content);

        ShowNoteMetadata(relativePath, content);
        ShowPreviewIfTheNoteHasDiagrams(content);
        ShowBacklinks(relativePath);
        ShowWordCount(content);

        if (PreviewHost.Visibility == Visibility.Visible) RenderPreview(content);

        // The history is a network read, so it is kicked off rather than awaited:
        // opening a note must not wait on the server, and the panel fills in when it
        // answers. RenderHistory is async void for that reason and only touches the
        // panel it owns.
        RenderHistory();

        if (_activeQuery is null && _activeTag is null) HighlightOpenNote();
    }

    // Moves the selection highlight to the open note by swapping two styles on
    // rows that already exist.
    //
    // It is deliberately not a full RenderTree. A row's Click handler runs while
    // the button is still part of its panel, and clearing that panel takes away
    // the very control the event is being dispatched from - a re-entrant mutation
    // that this runtime answers with a stowed native exception. Restyling the old
    // and new rows in place is also less disruptive: no flicker, and the tree
    // keeps its scroll position.
    private void HighlightOpenNote()
    {
        foreach (var (path, row) in _noteRows)
        {
            var wanted = (Style)Application.Current.Resources[
                path == _currentRelativePath ? "SidebarRowSelectedStyle" : "SidebarRowStyle"];

            if (!ReferenceEquals(row.Style, wanted)) row.Style = wanted;
        }
    }

    private void ShowWordCount(string content)
    {
        var words = content.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var characters = content.Length;
        StatusDetailText.Text = $"{words} words · {characters} chars";
    }

    // Read-only view of what the note declares: its own indexed metadata, its tags
    // (already normalized by TagParser), and any other top-level frontmatter fields
    // verbatim. Nothing here is interpreted or written back.
    private void ShowNoteMetadata(string? relativePath, string content)
    {
        if (relativePath is null)
        {
            NoteMetadataText.Text = string.Empty;
            return;
        }

        var lines = new List<string>();
        var note = _notesById.Values.FirstOrDefault(n => n.RelativePath == relativePath);
        if (note is not null)
        {
            lines.Add($"Path: {note.RelativePath}");
            lines.Add($"Revision {note.Revision}, updated {note.UpdatedAt.LocalDateTime:g}");
        }

        IReadOnlyList<string> tags = note is null ? [] : AppSession.OpenTagIndex().GetTagsForNote(note.Id);
        lines.Add(tags.Count == 0 ? "Tags: none" : $"Tags: {string.Join(' ', tags.Select(t => "#" + t))}");

        // `id` and `tags` are already shown above (and the id is machine noise in a
        // UI), so only the note's own custom fields are listed here.
        lines.AddRange(FrontMatter.ReadFields(content)
            .Where(field => field.Key != "id" && field.Key != "tags")
            .Select(field => $"{field.Key}: {field.Value}"));

        NoteMetadataText.Text = string.Join('\n', lines);
    }

    private void ShowBacklinks(string? relativePath)
    {
        var note = relativePath is null ? null : _notesById.Values.FirstOrDefault(n => n.RelativePath == relativePath);
        var backlinkNotes = note is null
            ? []
            : AppSession.OpenLinkIndex().GetBacklinkSourceIds(note.Id)
                .Select(id => _notesById.GetValueOrDefault(id))
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n.RelativePath)
                .ToList();

        BacklinksHeaderText.Text = backlinkNotes.Count switch
        {
            0 => "Nothing links here yet.",
            1 => "1 note links here:",
            _ => $"{backlinkNotes.Count} notes link here:",
        };

        BacklinksList.Children.Clear();
        foreach (var backlinkNote in backlinkNotes)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SidebarRowStyle"],
                Content = new TextBlock
                {
                    Text = Path.GetFileNameWithoutExtension(backlinkNote.RelativePath),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };

            var path = backlinkNote.RelativePath;
            button.Click += (_, _) => ShowAndEditNote(path);
            AddNoteContextMenu(button, path);
            BacklinksList.Children.Add(button);
        }
    }

    private void SaveCurrentNote()
    {
        if (_currentRelativePath is null || AppSession.VaultPath is null) return;

        try
        {
            var text = NormalizeLineEndings(Editor.Text);
            File.WriteAllText(ResolvePath(_currentRelativePath), text);

            var vaultIndex = AppSession.OpenVaultIndex();
            new VaultScanner(AppSession.VaultPath, vaultIndex).Scan();
            new LinkScanner(AppSession.VaultPath, vaultIndex, AppSession.OpenLinkIndex()).Scan();
            new SearchScanner(AppSession.VaultPath, vaultIndex, AppSession.OpenSearchIndex()).Scan();
            new TagScanner(AppSession.VaultPath, vaultIndex, AppSession.OpenTagIndex()).Scan();

            var notes = vaultIndex.GetAll().OrderBy(n => n.RelativePath).ToList();
            _notesById = notes.ToDictionary(n => n.Id);

            // The edit can have added or removed tags, so the tag panel and the
            // note's metadata are stale. The tree is not: writing to a note cannot
            // create, move or delete one, and this method also runs from inside the
            // click handler that opened the note, so it must not rebuild the panel
            // that click came from.
            PopulateTagsPanel();
            ShowNoteMetadata(_currentRelativePath, text);

            if (_activeQuery is not null || _activeTag is not null) RefreshView();
            else HighlightOpenNote();

            EditorStatusText.Text = $"Editing {_currentRelativePath} (saved {DateTime.Now:T})";
            StatusText.Text = "Saved.";

            ScheduleSyncAfterWrite();

            // The file on disk is what the preview shows, so it refreshes from what
            // was just written rather than from the text box's current contents.
            if (PreviewHost.Visibility == Visibility.Visible) RenderPreview(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            EditorStatusText.Text = $"Could not save {_currentRelativePath}: {ex.Message}";
        }
    }

    // WinUI's TextBox.Text getter can return CRLF-terminated lines even when the
    // control was only ever populated with LF content, so normalize before writing -
    // otherwise every save silently rewrites the file's line-ending style.
    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private string ResolvePath(string relativePath) =>
        Path.Combine(AppSession.VaultPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
