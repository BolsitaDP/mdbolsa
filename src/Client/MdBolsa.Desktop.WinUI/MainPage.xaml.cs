using MdBolsa.Core.Links;
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
using Windows.System;

namespace MdBolsa_Desktop_WinUI;

// Phase 3 editor + Phase 4 backlinks + Phase 5 search + Phase 6 tags/metadata:
// open a vault, browse, search or filter its notes by tag, edit one as plain
// Markdown text, save it (explicitly via SaveButton, or implicitly when
// switching notes/vaults), and see its tags, metadata and backlinks.
//
// Saving used to crash natively on this machine's WindowsAppSDK 2.4.0 (preview) build -
// reading Editor.Text back (the getter) triggered a STATUS_STOWED_EXCEPTION, reproduced
// with a no-op TextChanged handler, a polling DispatcherQueueTimer, GetValue instead of
// .Text, and RichEditBox.Document instead of TextBox (see docs/architecture.md's Known
// Issues section for the full isolation notes). Normalizing line endings before writing
// and hardening SqliteVaultIndex.Upsert against a stale path/id row (both below) is what
// was tried next, on a theory that the "crash" was actually an uncaught SqliteException
// getting misreported - that theory doesn't fully square with the isolation notes (which
// include a crash with a literally empty, no-op event handler touching neither the
// filesystem nor SQLite), but empirically, saving no longer crashes with these changes
// in place. Root cause still not confidently identified. Verified live: opening a note,
// typing a real edit, and clicking Save works and round-trips correctly on disk.
//
// Search deliberately has no live-as-you-type filtering: it's a button click, like
// every other action here, rather than a TextChanged-driven update - staying well away
// from anything resembling the pattern implicated above, even though TextChanged itself
// was never conclusively proven to be the (sole) cause.
//
// Phase 6 follows the same rule for the tag panel: clicking a tag button filters the
// notes list, clicking the same tag again clears it. Tag filtering and search are
// mutually exclusive views of the notes list (searching clears an active tag filter and
// says so) rather than a combined query, which keeps the state machine to one variable.
public sealed partial class MainPage : Page
{
    private string? _currentRelativePath;
    private string? _activeTag;
    private Dictionary<Guid, NoteMetadata> _notesById = new();

    public MainPage()
    {
        InitializeComponent();
        AppSession.Host = this; // so GraphPage's "Back to notes" can hand control back
        Loaded += OnLoaded;
    }

    // Reopens the last vault on startup, so the common case is zero clicks. Guarded
    // on VaultPath being null, because Loaded also fires when navigating back from
    // the graph page - the vault is still open then, and rescanning it again would
    // throw away the tag filter for no reason.
    //
    // The whole thing is deferred to the next dispatcher cycle, and that is not
    // cosmetic: doing it inline (setting VaultPathBox.Text and rescanning) reliably
    // reproduced the native STATUS_STOWED_EXCEPTION crash documented in
    // docs/architecture.md's Known Issues - 0xc000027b in Microsoft.UI.Xaml.dll -
    // because mutating a control while the tree is still handling Loaded is
    // re-entrant. Same reason ShowAndEditNote defers Editor.Text.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is not null) return;

        var remembered = AppSession.RecallVaultPath();
        if (string.IsNullOrWhiteSpace(remembered)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            VaultPathBox.Text = remembered;

            if (Directory.Exists(remembered))
            {
                OpenVault(remembered);
            }
            else
            {
                StatusText.Text = $"Last vault is not there anymore: {remembered}. Pick another one with Browse.";
            }
        });
    }

    private void OnOpenVaultClicked(object sender, RoutedEventArgs e) => OpenVault(VaultPathBox.Text.Trim());

    // Windows' own folder picker, rather than making the user find and paste a path.
    //
    // This is Microsoft.Windows.Storage.Pickers.FolderPicker, not the UWP
    // Windows.Storage.Pickers one: the desktop picker takes the window id in its
    // constructor, while the UWP picker has no way to be told which window owns
    // the dialog and simply never shows one (verified: it hangs on
    // PickSingleFolderAsync with no dialog and no exception, which is why the
    // earlier status-bar error was all we ever saw).
    //
    // The chosen path is passed straight to OpenVault instead of being read back
    // out of the text box - no reason to round-trip a value we just set ourselves.
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

            VaultPathBox.Text = folder.Path;
            OpenVault(folder.Path);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not open the folder picker: {ex.Message}";
        }
    }

    private void OpenVault(string path)
    {
        SaveCurrentNote();
        _currentRelativePath = null;
        _activeTag = null;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "Pick a vault folder with Browse..., or type a path, then click Open vault.";
            return;
        }

        AppSession.VaultPath = path;
        AppSession.RememberVaultPath(path);
        RescanAndRefreshList();
    }

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
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveCurrentNote();

    // --- Sync (Phase 9) ----------------------------------------------------

    // Sync settings are edited inline (no dialog - popups crash this runtime).
    private void OnSyncSettingsClicked(object sender, RoutedEventArgs e)
    {
        var showing = SyncSettingsPanel.Visibility == Visibility.Visible;
        if (showing)
        {
            SyncSettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SyncServerBox.Text = AppSession.ServerUrl ?? string.Empty;
        SyncTokenBox.Password = AppSession.ServerToken ?? string.Empty;
        SyncSettingsPanel.Visibility = Visibility.Visible;
    }

    private void OnSaveSyncSettingsClicked(object sender, RoutedEventArgs e)
    {
        var url = SyncServerBox.Text.Trim();
        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            StatusText.Text = "The sync server URL has to be an http(s) address, e.g. http://localhost:5080";
            return;
        }

        AppSession.ServerUrl = url.Length == 0 ? null : url;
        AppSession.ServerToken = SyncTokenBox.Password.Length == 0 ? null : SyncTokenBox.Password;
        SyncSettingsPanel.Visibility = Visibility.Collapsed;

        StatusText.Text = AppSession.ServerUrl is null
            ? "Sync is not configured. Press Sync settings... to set a server."
            : $"Sync configured for {AppSession.ServerUrl} (device {AppSession.DeviceId.ToString()[..8]}).";
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        if (!TryCreateSyncClient(out var client, out var error))
        {
            StatusText.Text = error;
            return;
        }

        SyncButton.IsEnabled = false;
        SyncButton.Content = "Syncing...";

        try
        {
            // The local indexes have to be current *before* the sync: the push
            // side works from the note list, and a note saved since the last scan
            // would otherwise not be offered to the server.
            RescanAndRefreshList();

            var result = await client.SyncAsync(AppSession.VaultPath);

            if (result.Failed)
            {
                StatusText.Text = $"Sync failed: {result.Error}";
                return;
            }

            // The pull may have written or deleted .md files, so the indexes and
            // every panel derived from them are stale until we scan again.
            RescanAndRefreshList();

            var conflicts = AppSession.OpenSyncStateStore().GetConflicts();
            StatusText.Text = conflicts.Count == 0
                ? $"Synced: {result.Pulled} pulled, {result.Pushed} pushed, {result.Deleted} deleted."
                : $"Synced: {result.Pulled} pulled, {result.Pushed} pushed, {result.Deleted} deleted. " +
                  $"{conflicts.Count} conflict(s) changed in two places - nothing was overwritten: " +
                  string.Join("; ", conflicts.Take(3).Select(c => c.ToString()));
        }
        finally
        {
            SyncButton.IsEnabled = true;
            SyncButton.Content = "Sync";
        }
    }

    private bool TryCreateSyncClient(out NoteSyncClient client, out string error)
    {
        client = null!;
        error = string.Empty;

        var url = AppSession.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Sync is not configured. Press Sync settings... to set a server.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AppSession.ServerToken))
        {
            error = "No sync token configured. Press Sync settings... and enter the server's token.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            error = $"\"{url}\" is not a valid sync server address.";
            return false;
        }

        // A base address without a trailing slash makes every relative request
        // drop its last segment, which looks like a 404 for the wrong endpoint.
        if (!url.EndsWith('/')) baseAddress = new Uri(url + "/");

        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client = new NoteSyncClient(
            http,
            AppSession.DeviceId,
            AppSession.ServerToken,
            AppSession.OpenSyncStateStore(),
            AppSession.OpenVaultIndex());

        return true;
    }
    // Phase 7's graph view. It is hosted in a Frame inside this page, *not* reached
    // by navigating the root Frame: that unloads MainPage, and unloading a page
    // holding the Editor TextBox crashes the process natively
    // (STATUS_STOWED_EXCEPTION, 0xc000027b) - verified by bisection, with an empty
    // GraphPage and an empty OnLoaded, so it is the teardown and not the graph.
    // MainContent is only hidden, so the editor is never torn down. See
    // docs/architecture.md's Known Issues.
    private void OnGraphClicked(object sender, RoutedEventArgs e)
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

    // Called by GraphPage's "Back to notes" button.
    public void HideGraph()
    {
        GraphHost.Visibility = Visibility.Collapsed;
        MainContent.Visibility = Visibility.Visible;
        GraphHost.BackStack.Clear();
    }

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        var clearedTagFilter = _activeTag is not null;
        _activeTag = null;
        PopulateTagsPanel();

        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            RefreshNotesList();
            return;
        }

        var results = AppSession.OpenSearchIndex().Search(query);
        var clearedNote = clearedTagFilter ? " Tag filter cleared." : string.Empty;
        StatusText.Text = $"{results.Count} search result(s) for \"{query}\".{clearedNote}";

        NotesList.Children.Clear();
        if (results.Count == 0)
        {
            NotesList.Children.Add(new TextBlock { Text = "No matches.", Opacity = 0.7 });
            return;
        }

        foreach (var result in results)
        {
            if (!_notesById.TryGetValue(result.NoteId, out var note)) continue;

            var button = new Button
            {
                Content = $"{note.RelativePath}\n{result.Snippet}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => ShowAndEditNote(note.RelativePath);
            AddNoteContextMenu(button, note.RelativePath);
            NotesList.Children.Add(button);
        }
    }

    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _activeTag = null;
        PopulateTagsPanel();
        RefreshNotesList();
    }

    // Tag buttons are built in code, so this is called from the click handler
    // rather than wired up in XAML.
    private void OnTagClicked(string tag)
    {
        _activeTag = _activeTag == tag ? null : tag;
        PopulateTagsPanel();
        RefreshNotesList();
    }

    private void RescanAndRefreshList()
    {
        var vaultIndex = AppSession.OpenVaultIndex();
        var vaultResult = new VaultScanner(AppSession.VaultPath!, vaultIndex).Scan();

        var linkIndex = AppSession.OpenLinkIndex();
        var linkResult = new LinkScanner(AppSession.VaultPath!, vaultIndex, linkIndex).Scan();

        new SearchScanner(AppSession.VaultPath!, vaultIndex, AppSession.OpenSearchIndex()).Scan();

        var tagResult = new TagScanner(AppSession.VaultPath!, vaultIndex, AppSession.OpenTagIndex()).Scan();

        var notes = vaultIndex.GetAll().OrderBy(n => n.RelativePath).ToList();
        _notesById = notes.ToDictionary(n => n.Id);

        PopulateTagsPanel();
        RefreshNotesList(
            $"Notes: added {vaultResult.Added}, updated {vaultResult.Updated}, " +
            $"moved {vaultResult.Moved}, deleted {vaultResult.Deleted}, " +
            $"unchanged {vaultResult.Unchanged}. Links: {linkResult.Resolved} resolved, " +
            $"{linkResult.Unresolved} unresolved. Tags: {tagResult.TaggedNotes} tagged " +
            $"note(s), {tagResult.DistinctTags} distinct.");
    }

    // Rebuilds the notes list from the current view state: everything, or just the
    // notes carrying the active tag. statusPrefix lets a scan keep its summary
    // visible instead of being replaced by the list description.
    private void RefreshNotesList(string? statusPrefix = null)
    {
        var notes = _notesById.Values.OrderBy(n => n.RelativePath).ToList();

        string status;
        if (_activeTag is not null)
        {
            var taggedNoteIds = AppSession.OpenTagIndex().GetNoteIdsForTag(_activeTag).ToHashSet();
            notes = notes.Where(n => taggedNoteIds.Contains(n.Id)).ToList();
            status = $"Filtering by #{_activeTag}: {notes.Count} note(s). Click the tag again to clear.";
        }
        else
        {
            status = $"Showing all {notes.Count} note(s).";
        }

        PopulateNotesList(notes);
        StatusText.Text = statusPrefix is null ? status : $"{statusPrefix} {status}";
    }

    private void PopulateTagsPanel()
    {
        TagsList.Children.Clear();

        if (AppSession.VaultPath is null)
        {
            TagsHeaderText.Text = "No tags yet.";
            return;
        }

        var tagCounts = AppSession.OpenTagIndex().GetTagCounts();
        TagsHeaderText.Text = _activeTag is not null
            ? $"Tags - showing notes tagged #{_activeTag}. Click it again to clear:"
            : tagCounts.Count == 0
                ? "No tags yet."
                : $"Tags ({tagCounts.Count}):";

        foreach (var tagCount in tagCounts)
        {
            var button = new Button
            {
                Content = $"#{tagCount.Tag} ({tagCount.NoteCount})",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            var tag = tagCount.Tag;
            button.Click += (_, _) => OnTagClicked(tag);
            TagsList.Children.Add(button);
        }
    }

    // Plain Buttons in a StackPanel, not a ListView - a ListView (with or without
    // SelectionChanged/ItemClick, with or without a handler that does anything) was
    // never actually the problem; the Editor TextBox save path was. Kept as Buttons
    // since they're already proven stable here and are plenty for personal-vault
    // scale; revisit if a later phase needs virtualization for very large vaults.
    private void PopulateNotesList(IEnumerable<NoteMetadata> notes)
    {
        NotesList.Children.Clear();
        foreach (var note in notes)
        {
            var button = new Button
            {
                Content = note.RelativePath,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => ShowAndEditNote(note.RelativePath);
            AddNoteContextMenu(button, note.RelativePath);
            NotesList.Children.Add(button);
        }
    }

    // Right-click starts a rename on any note entry - the notes list, search results
    // and the backlinks panel.
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

    private TextBox? _renameBox;
    private FrameworkElement? _renameRow;
    private string? _renamePath;
    private bool _renameSettled;

    // Swaps the note's row for a text box: Enter commits, Escape cancels, and losing
    // focus cancels too (with _renameSettled keeping the two from fighting).
    private void BeginInlineRename(FrameworkElement row, string relativePath)
    {
        CancelInlineRename();

        if (row.Parent is not Panel parent) return;
        var index = parent.Children.IndexOf(row);
        if (index < 0) return;

        var input = new TextBox
        {
            Text = Path.GetFileName(relativePath),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new FontFamily("Consolas"),
        };
        input.Select(0, input.Text.Length);
        input.KeyDown += OnRenameKeyDown;
        input.LostFocus += (_, _) => CancelInlineRename();

        _renameBox = input;
        _renameRow = row;
        _renamePath = relativePath;
        _renameSettled = false;

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, input);
        input.Focus(FocusState.Programmatic);
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitInlineRename();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CancelInlineRename();
                e.Handled = true;
                break;
        }
    }

    private void CommitInlineRename()
    {
        if (_renameSettled || _renameBox is null || _renamePath is null) return;

        RenameNote(_renamePath, _renameBox.Text);
    }

    private void CancelInlineRename()
    {
        if (_renameSettled) return;
        _renameSettled = true;
        RestoreRenameRow();
    }

    private void RestoreRenameRow()
    {
        if (_renameBox is not null && _renameRow?.Parent is Panel parent)
        {
            var index = parent.Children.IndexOf(_renameBox);
            if (index >= 0)
            {
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, _renameRow);
            }
        }

        _renameBox = null;
        _renameRow = null;
        _renamePath = null;
        _renameSettled = false;
    }

    // Renames the note's file. The note keeps its identity: `id` lives in the
    // frontmatter, not in the filename, so the vault scanner reconciles this as a
    // move rather than a delete + create, and links resolve against the new path on
    // the next scan. Links that pointed at the *old* name stop resolving - the app
    // deliberately doesn't rewrite note content (see docs/decisions/0008), so the
    // rescan's unresolved-link count in the status line is how you find them.
    private void RenameNote(string relativePath, string requestedName)
    {
        if (AppSession.VaultPath is null) return;

        var currentName = Path.GetFileName(relativePath);
        var destination = Path.Combine(AppSession.VaultPath, NoteRename.Normalize(requestedName));
        if (!NoteRename.TryResolve(
                currentName, requestedName, File.Exists(destination), out var newName, out var error))
        {
            RestoreRenameRow();
            StatusText.Text = error;
            return;
        }

        RestoreRenameRow();

        try
        {
            File.Move(ResolvePath(relativePath), Path.Combine(AppSession.VaultPath, newName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not rename {currentName}: {ex.Message}";
            return;
        }

        // Drop the editor's claim on the old path before rescanning, or the next
        // save would write the note straight back to the old filename.
        _currentRelativePath = null;
        AppSession.CurrentNoteRelativePath = null;

        RescanAndRefreshList();
        ShowAndEditNote(newName);
        StatusText.Text = $"Renamed {currentName} to {newName}.";
    }

    private void ShowAndEditNote(string? relativePath)
    {
        SaveCurrentNote();
        _currentRelativePath = relativePath;

        // The file can vanish between being listed and being opened (external delete,
        // rename by another tool) - a real possibility for a local-first app that
        // doesn't own exclusive access to the vault.
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
        EditorStatusText.Text = relativePath is null ? "Select a note to edit." : $"Editing {relativePath}";

        // Deferred to the next dispatcher cycle - setting Editor.Text synchronously
        // inline in the click/scan call stack was part of what reproduced the crash
        // described above.
        DispatcherQueue.TryEnqueue(() => Editor.Text = content);

        ShowNoteMetadata(relativePath, content);
        ShowBacklinks(relativePath);
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
            0 => "No backlinks.",
            1 => "1 backlink:",
            _ => $"{backlinkNotes.Count} backlinks:",
        };

        BacklinksList.Children.Clear();
        foreach (var backlinkNote in backlinkNotes)
        {
            var button = new Button
            {
                Content = backlinkNote.RelativePath,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => ShowAndEditNote(backlinkNote.RelativePath);
            AddNoteContextMenu(button, backlinkNote.RelativePath);
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

            // The edit can have added or removed tags, so the tag panel, the note's
            // metadata line and the (possibly tag-filtered) list are all stale now.
            PopulateTagsPanel();
            ShowNoteMetadata(_currentRelativePath, text);
            RefreshNotesList();

            EditorStatusText.Text = $"Editing {_currentRelativePath} (saved {DateTime.Now:T})";
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
