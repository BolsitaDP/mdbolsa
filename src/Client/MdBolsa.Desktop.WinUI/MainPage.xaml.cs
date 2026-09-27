using MdBolsa.Contracts;
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
using Windows.Foundation;
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

    public MainPage()
    {
        InitializeComponent();
        AppSession.Host = this; // so GraphPage's "Back to notes" can hand control back
        Loaded += OnLoaded;
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

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        FocusSearch();

    // Ctrl+P is Obsidian's quick switcher. There isn't one yet, and sending it to
    // search is the nearest honest thing: both are "type a name, go there".
    private void OnQuickSwitcherAccelerator(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        FocusSearch();

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


    // --- Sidebar views ------------------------------------------------------

    // The sidebar shows one of four things - the tree, a search or tag result, the
    // sync settings, or the conflicts - and switches between them. Only one is
    // visible at a time, which is why nothing here has to agree about space.
    private void ShowSidebarView(UIElement? view)
    {
        foreach (var candidate in new UIElement?[] { TreeList, FlatListPanel, SyncSettingsPanel, ConflictsPanel })
        {
            if (candidate is null) continue;
            candidate.Visibility = ReferenceEquals(candidate, view) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowTree()
    {
        _activeQuery = null;
        _activeTag = null;
        RenderTree();
        ShowSidebarView(TreeList);
    }

    private void OnNotesRibbonClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        PopulateTagsPanel();
        ShowTree();
    }

    private void OnToggleLeftSidebarClicked(object sender, RoutedEventArgs e) => ToggleLeftSidebar();

    private void ToggleLeftSidebar()
    {
        var hiding = LeftSidebar.Visibility == Visibility.Visible;
        LeftSidebar.Visibility = hiding ? Visibility.Collapsed : Visibility.Visible;

    }

    private void OnToggleRightSidebarClicked(object sender, RoutedEventArgs e) => ToggleRightSidebar();

    private void ToggleRightSidebar()
    {
        var hiding = RightSidebar.Visibility == Visibility.Visible;
        RightSidebar.Visibility = hiding ? Visibility.Collapsed : Visibility.Visible;

    }

    // The vault as a folder tree, rendered flat with one row per node and an
    // indent per level. A flat list rather than nested TreeViews because the rows
    // have to be swappable in and out for the inline rename, which means each row
    // has to be a direct child of the panel that holds it - and because a vault of
    // personal notes is hundreds of rows, not hundreds of thousands.
    private void RenderTree()
    {
        TreeList.Children.Clear();
        _noteRows.Clear();
        if (AppSession.VaultPath is null) return;

        var tree = VaultTree.Build(_notesById.Values);
        AddTreeRows(TreeList, tree, depth: 0, folderPath: string.Empty);

        StatusDetailText.Text = $"{_notesById.Count} notes";
    }

    private void AddTreeRows(Panel panel, IReadOnlyList<VaultTreeNode> nodes, int depth, string folderPath)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                var childPath = folderPath.Length == 0 ? node.Name : $"{folderPath}/{node.Name}";
                var collapsed = _collapsedFolders.Contains(childPath);

                var folderRow = new Button
                {
                    Style = (Style)Application.Current.Resources["SidebarRowStyle"],
                    Margin = new Thickness(0, 1, 0, 1),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Grid
                            {
                                Width = 12,
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    // A chevron that points right when folded, down when
                                    // open. Drawn rather than typed so it can rotate.
                                    new PathShape
                                    {
                                        Data = ChevronGeometry(),
                                        RenderTransform = new RotateTransform
                                        {
                                            // 0 points right (folded), 90 points down (open).
                                            Angle = collapsed ? 0 : 90,
                                            CenterX = 6,
                                            CenterY = 6,
                                        },
                                        Fill = (Brush)Application.Current.Resources["ChevronBrush"],
                                    },
                                },
                            },
                            new TextBlock
                            {
                                Text = node.Name,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                FontSize = 13,
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    },
                };

                var captured = childPath;
                folderRow.Click += (_, _) =>
                {
                    if (!_collapsedFolders.Remove(captured)) _collapsedFolders.Add(captured);

                    // Deferred, and not for timing: the row being clicked is a child
                    // of the tree this rebuilds, so clearing the tree now would remove
                    // the control whose event is still being dispatched. Same
                    // re-entrancy that crashes on opening a note.
                    DispatcherQueue.TryEnqueue(RenderTree);
                };

                panel.Children.Add(folderRow);

                // A collapsed folder's children aren't built at all, which is what
                // keeps a big vault from creating a control per hidden row.
                if (!collapsed) AddTreeRows(panel, node.Children, depth + 1, childPath);
                continue;
            }

            var relativePath = node.RelativePath!;
            var isOpen = relativePath == _currentRelativePath;

            var noteRow = new Button
            {
                Style = (Style)Application.Current.Resources[
                    isOpen ? "SidebarRowSelectedStyle" : "SidebarRowStyle"],
                Margin = new Thickness(4 + depth * 14, 1, 0, 1),
                Content = new TextBlock
                {
                    Text = node.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };


            var path = relativePath;
            noteRow.Click += (_, _) => ShowAndEditNote(path);
            AddNoteContextMenu(noteRow, path);
            _noteRows[path] = noteRow;
            panel.Children.Add(noteRow);
        }
    }

    // --- Search and tags ----------------------------------------------------

    // Search is applied on Enter, never on every keystroke. Live filtering means
    // rebuilding a list of controls from inside a TextChanged handler, and this
    // app's crash history (docs/architecture.md) makes anything that mutates
    // controls from a text input's own event a thing to avoid on purpose rather
    // than by luck.
    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        RunSearch();
    }



    private void RunSearch()
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            OnClearSearchClicked(this, new RoutedEventArgs());
            return;
        }

        // Searching and tag-filtering are two views of the same panel, not a
        // combined query, which keeps the state to one variable and the panel to
        // one list.
        var clearedTag = _activeTag is not null;
        _activeTag = null;
        _activeQuery = query;
        PopulateTagsPanel();

        var results = AppSession.OpenSearchIndex().Search(query);
        var notes = results
            .Select(result => _notesById.GetValueOrDefault(result.NoteId))
            .Where(note => note is not null)
            .Select(note => note!)
            .ToList();

        StatusText.Text = $"{notes.Count} result(s) for \"{query}\"." +
            (clearedTag ? " Tag filter cleared." : string.Empty);

        PopulateFlatList(notes, snippetFor: query, header: $"SEARCH  \"{query}\"  ·  {notes.Count} match(es)");
        ShowSidebarView(FlatListPanel);
    }

    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ShowTree();
        StatusText.Text = $"Showing all {_notesById.Count} note(s).";
    }

    // Tag buttons are built in code, so this is called from the click handler
    // rather than wired up in XAML.
    private void OnTagClicked(string tag)
    {
        _activeTag = _activeTag == tag ? null : tag;
        _activeQuery = null;
        PopulateTagsPanel();

        if (_activeTag is null)
        {
            ShowTree();
            StatusText.Text = $"Showing all {_notesById.Count} note(s).";
            return;
        }

        var tagged = AppSession.OpenTagIndex().GetNoteIdsForTag(_activeTag).ToHashSet();
        var notes = _notesById.Values
            .Where(note => tagged.Contains(note.Id))
            .OrderBy(note => note.RelativePath)
            .ToList();

        PopulateFlatList(notes, snippetFor: null, header: $"#{_activeTag}  ·  {notes.Count} note(s)");
        ShowSidebarView(FlatListPanel);
        StatusText.Text = $"Filtering by #{_activeTag}. Click the tag again to clear.";
    }

    // One row list, used by search results, tag results and backlinks. Snippet is
    // the matched line for search and nothing for the others, so a result says why
    // it matched when there is a "why".
    private void PopulateFlatList(
        IReadOnlyList<NoteMetadata> notes, string? snippetFor, string header)
    {
        NotesList.Children.Clear();
        FlatListHeader.Text = header;

        if (notes.Count == 0)
        {
            NotesList.Children.Add(new TextBlock
            {
                Text = "Nothing here.",
                Opacity = 0.6,
                FontSize = 12,
                Margin = new Thickness(8, 4, 0, 0),
            });
            return;
        }

        foreach (var note in notes)
        {
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(note.RelativePath),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (snippetFor is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = SnippetFor(note, snippetFor),
                    FontSize = 11,
                    Opacity = 0.65,
                    MaxLines = 2,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SidebarRowStyle"],
                Content = content,
            };

            var path = note.RelativePath;
            button.Click += (_, _) => ShowAndEditNote(path);
            AddNoteContextMenu(button, path);
            NotesList.Children.Add(button);
        }
    }

    // The line a note matched on, with the match marked by its position rather than
    // by markup - the sidebar has no room for formatting, and the line itself is
    // usually enough to recognise the note.
    private string SnippetFor(NoteMetadata note, string query)
    {
        if (AppSession.VaultPath is null) return string.Empty;

        try
        {
            var content = File.ReadAllText(ResolvePath(note.RelativePath));
            var line = content
                .Split('\n')
                .FirstOrDefault(candidate =>
                    candidate.Contains(query, StringComparison.OrdinalIgnoreCase));

            return (line ?? content.Split('\n').FirstOrDefault(c => c.Trim().Length > 0) ?? string.Empty).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // Read-only view of the tags in the vault. A vertical list, not a wrap panel:
    // tags are clickable filters, and a clickable pill that reflows as you type
    // them is a misclick waiting to happen.
    private void PopulateTagsPanel()
    {
        TagsList.Children.Clear();

        if (AppSession.VaultPath is null)
        {
            TagsHeaderText.Text = "TAGS";
            return;
        }

        var tagCounts = AppSession.OpenTagIndex().GetTagCounts();

        TagsHeaderText.Text = tagCounts.Count == 0
            ? "TAGS  ·  none yet"
            : _activeTag is not null
                ? $"TAGS  ·  showing #{_activeTag}"
                : $"TAGS  ·  {tagCounts.Count}";

        foreach (var tagCount in tagCounts)
        {
            var tag = tagCount.Tag;

            var button = new Button
            {
                Style = (Style)Application.Current.Resources["SidebarRowStyle"],
                Content = new TextBlock
                {
                    Text = $"#{tag}  ({tagCount.NoteCount})",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };

            // The active tag is tinted, so it's obvious which filter is on without
            // reading the status line.
            if (_activeTag == tag)
            {
                button.Style = (Style)Application.Current.Resources["SidebarRowSelectedStyle"];
            }

            button.Click += (_, _) => OnTagClicked(tag);
            TagsList.Children.Add(button);
        }
    }

    // --- Sync (Phase 9) ----------------------------------------------------

    private void OnSyncRibbonClicked(object sender, RoutedEventArgs e) => _ = SyncNowAsync();

    // Sync settings are edited inline: no dialogs on this runtime.
    private void OnSyncSettingsRibbonClicked(object sender, RoutedEventArgs e)
    {
        if (SyncSettingsPanel.Visibility == Visibility.Visible)
        {
            ShowTree();
            return;
        }

        SyncServerBox.Text = AppSession.ServerUrl ?? string.Empty;
        SyncTokenBox.Password = AppSession.ServerToken ?? string.Empty;
        ShowSidebarView(SyncSettingsPanel);
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

        ShowTree();
        StatusText.Text = AppSession.ServerUrl is null
            ? "Sync is not configured. The sliders button in the ribbon sets a server."
            : $"Sync configured for {AppSession.ServerUrl} (device {AppSession.DeviceId.ToString()[..8]}).";
    }

    private async Task SyncNowAsync()
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

        SyncRibbonButton.IsEnabled = false;
        StatusText.Text = "Syncing...";

        try
        {
            // The local indexes have to be current *before* the sync: the push side
            // works from the note list, and a note saved since the last scan would
            // otherwise not be offered to the server.
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
                  $"{conflicts.Count} note(s) changed in two places - nothing was overwritten. " +
                  "The warning button in the ribbon lists them.";
        }
        finally
        {
            SyncRibbonButton.IsEnabled = true;
        }
    }



    private bool TryCreateSyncClient(out NoteSyncClient client, out string error)
    {
        client = null!;
        error = string.Empty;

        var url = AppSession.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Sync is not configured. The sliders button in the ribbon sets a server and a token.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AppSession.ServerToken))
        {
            error = "No sync token configured. The sliders button in the ribbon sets one.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            error = $"\"{url}\" is not a valid sync server address.";
            return false;
        }

        // A base address without a trailing slash makes every relative request drop
        // its last segment, which looks like a 404 for the wrong endpoint.
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

    // --- Note history (Phase 10, client side) ------------------------------

    // The server keeps every revision and the client can ask for them; until this
    // existed, neither the history nor a way back existed in the UI, which is half
    // of what Phase 10 promised. Resolving a conflict was possible; undoing one
    // edit was not.
    //
    // It lives in the right sidebar because it is context *for the open note*, not
    // a view of the vault - the sidebar's single content area belongs to the file
    // list, and mixing them would put "where my notes are" and "how this note got
    // here" in the same column.
    // Bumped by every render. Two history loads can be in flight at once - the
    // panel is (deliberately) not awaited when a note is opened, so switching notes
    // quickly starts a second one - and whichever finishes second wins. Without this,
    // the slower response appends its cards to the newer one's list and you get two
    // of everything.
    private int _historyGeneration;

    private async void RenderHistory()
    {
        var generation = ++_historyGeneration;
        HistoryList.Children.Clear();

        var relativePath = _currentRelativePath;
        if (relativePath is null)
        {
            HistoryHintText.Text = "Open a note to see its revisions.";
            HistoryHintText.Visibility = Visibility.Visible;
            return;
        }

        var note = _notesById.Values.FirstOrDefault(n => n.RelativePath == relativePath);
        if (note is null)
        {
            HistoryHintText.Text = string.Empty;
            HistoryHintText.Visibility = Visibility.Collapsed;
            return;
        }

        // No server, no history - and saying so is better than an empty panel that
        // looks like a note with no past.
        if (!TryCreateConflictResolver(out var resolver, out var error))
        {
            HistoryHintText.Text = "Set up sync to keep a history of this note.";
            HistoryHintText.Visibility = Visibility.Visible;
            return;
        }

        IReadOnlyList<NoteVersion> history;
        try
        {
            history = await resolver.GetHistoryAsync(note.Id);
        }
        catch (SyncException ex) when (generation == _historyGeneration)
        {
            HistoryHintText.Text = $"Could not read the history: {ex.Message}";
            HistoryHintText.Visibility = Visibility.Visible;
            return;
        }
        catch (SyncException)
        {
            // A stale request's failure is not news: something newer is already on
            // screen, and saying otherwise would be a lie about the current note.
            return;
        }

        if (generation != _historyGeneration) return;

        HistoryHintText.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryHintText.Text = history.Count == 0
            ? "No revisions on the server yet. The first one appears after the next sync."
            : string.Empty;

        foreach (var version in history)
        {
            HistoryList.Children.Add(BuildHistoryCard(resolver, note, version));
        }
    }

    private Border BuildHistoryCard(
        ConflictResolver resolver, NoteMetadata note, NoteVersion version)
    {
        var body = new StackPanel { Spacing = 6 };

        var heading = new Grid { ColumnSpacing = 6 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = $"rev {version.Revision} · {version.UpdatedAt.LocalDateTime:g}",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);
        heading.Children.Add(label);

        // Which side wrote it, and whether it is what the server holds now. Both
        // matter when you are deciding whether a revision is the one you lost.
        var origin = new TextBlock
        {
            Text = version.IsCurrent ? "current" : version.DeviceId.ToString()[..6],
            FontSize = 11,
            Opacity = 0.6,
        };
        Grid.SetColumn(origin, 1);
        heading.Children.Add(origin);
        body.Children.Add(heading);

        // The content, hidden until asked for. A sidebar has no room for two
        // revisions side by side, and this is a read, not an editor.
        var preview = new TextBlock
        {
            Text = version.Content,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
            MaxHeight = 220,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var show = new Button { Content = "Show", FontSize = 12 };
        show.Click += (_, _) => preview.Visibility = preview.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        buttons.Children.Add(show);

        if (!version.IsCurrent)
        {
            var restore = new Button { Content = "Restore", FontSize = 12 };
            var requested = version.Revision;
            restore.Click += async (_, _) => await RestoreRevisionAsync(resolver, note, requested, restore);
            buttons.Children.Add(restore);
        }

        body.Children.Add(buttons);
        body.Children.Add(preview);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardStyle"],
            Child = body,
        };
    }

    private async Task RestoreRevisionAsync(
        ConflictResolver resolver, NoteMetadata note, int revision, Button button)
    {
        if (AppSession.VaultPath is null) return;

        button.IsEnabled = false;
        StatusText.Text = $"Restoring revision {revision}...";

        var result = await resolver.RestoreAsync(AppSession.VaultPath, note.Id, revision);

        if (!result.Resolved)
        {
            button.IsEnabled = true;
            StatusText.Text = $"Could not restore revision {revision}: {result.Error}";
            return;
        }

        // The file changed under the editor, so the indexes, the tree and the open
        // note all have to be re-read rather than patched.
        RescanAndRefreshList();
        ShowAndEditNote(note.RelativePath);
        RenderHistory();

        StatusText.Text = revision == note.Revision
            ? "That was already the current revision."
            : $"Restored revision {revision}. It is now the note's current content and the next " +
              "sync will push it as a new revision - nothing was removed from the history.";
    }

    // --- Conflicts (Phase 10) ----------------------------------------------

    private void OnConflictsRibbonClicked(object sender, RoutedEventArgs e)
    {
        if (ConflictsPanel.Visibility == Visibility.Visible)
        {
            ShowTree();
            return;
        }

        RenderConflicts();
        ShowSidebarView(ConflictsPanel);
    }

    // One card per conflicted note, with the two choices that exist. There is no
    // merge button, on purpose: an automatic merge of two Markdown files needs a
    // common ancestor and real diffing, and a wrong automatic merge is worse than
    // none. See docs/decisions/0012-conflict-resolution.md.
    private void RenderConflicts()
    {
        ConflictsList.Children.Clear();

        var conflicts = AppSession.OpenSyncStateStore().GetConflicts();
        ConflictsSummaryText.Text = conflicts.Count == 0
            ? "No conflicts. Every note matches the server."
            : $"{conflicts.Count} note(s) changed in two places. Nothing has been overwritten - " +
              "choose which version to keep.";

        foreach (var conflict in conflicts)
        {
            var body = new StackPanel { Spacing = 6 };
            var card = new Border
            {
                Style = (Style)Application.Current.Resources["CardStyle"],
                Child = body,
            };

            body.Children.Add(new TextBlock
            {
                Text = conflict.RelativePath ?? conflict.NoteId.ToString(),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = $"yours rev {conflict.LocalRevision} · theirs rev {conflict.ServerRevision}",
                FontSize = 11,
                Opacity = 0.6,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var noteId = conflict.NoteId;

            var keepMine = new Button { Content = "Keep mine", FontSize = 12 };
            keepMine.Click += async (_, _) => await ResolveConflictAsync(noteId, keepLocal: true);
            buttons.Children.Add(keepMine);

            var takeTheirs = new Button { Content = "Take theirs", FontSize = 12 };
            takeTheirs.Click += async (_, _) => await ResolveConflictAsync(noteId, keepLocal: false);
            buttons.Children.Add(takeTheirs);

            body.Children.Add(buttons);
            ConflictsList.Children.Add(card);
        }
    }

    private async Task ResolveConflictAsync(Guid noteId, bool keepLocal)
    {
        if (AppSession.VaultPath is null) return;

        if (!TryCreateConflictResolver(out var resolver, out var error))
        {
            StatusText.Text = error;
            return;
        }

        var result = keepLocal
            ? await resolver.KeepLocalAsync(AppSession.VaultPath, noteId)
            : await resolver.TakeRemoteAsync(AppSession.VaultPath, noteId);

        if (!result.Resolved)
        {
            StatusText.Text = $"Could not resolve the conflict: {result.Error}";
            return;
        }

        // Both choices change what the scanners would see (a new file, or a
        // different one), so re-index and redraw rather than guess.
        RescanAndRefreshList();
        RenderConflicts();
        RenderHistory();
        ShowNoteMetadata(_currentRelativePath, ReadCurrentNoteContent() ?? string.Empty);

        StatusText.Text = keepLocal
            ? "Kept this device's version and pushed it. The server's version is in the note's history."
            : "Took the server's version. Your copy is in the note's history if you need it.";
    }

    private string? ReadCurrentNoteContent()
    {
        if (_currentRelativePath is null || AppSession.VaultPath is null) return string.Empty;
        try
        {
            return File.ReadAllText(ResolvePath(_currentRelativePath));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private bool TryCreateConflictResolver(out ConflictResolver resolver, out string error)
    {
        resolver = null!;

        var url = AppSession.ServerUrl;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            error = "No usable sync server configured. The sliders button in the ribbon sets one.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AppSession.ServerToken))
        {
            error = "No sync token configured. The sliders button in the ribbon sets one.";
            return false;
        }

        if (!url.EndsWith('/')) baseAddress = new Uri(url + "/");
        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        resolver = new ConflictResolver(
            http,
            AppSession.DeviceId,
            AppSession.ServerToken,
            AppSession.OpenSyncStateStore(),
            AppSession.OpenVaultIndex());

        error = string.Empty;
        return true;
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
            $"{tagResult.DistinctTags} tag(s).");
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

            PopulateFlatList(notes, _activeQuery, $"SEARCH  \"{_activeQuery}\"  ·  {notes.Count} match(es)");
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

            PopulateFlatList(notes, snippetFor: null, header: $"#{_activeTag}  ·  {notes.Count} note(s)");
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

    private TextBox? _renameBox;
    private FrameworkElement? _renameRow;
    private string? _renamePath;
    private bool _renameSettled;

    // Swaps the note's row for a text box: Enter commits, Escape cancels, and losing
    // focus cancels too (with _renameSettled keeping the two from fighting).
    public void BeginInlineRename(FrameworkElement row, string relativePath)
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

        // Drop the editor's claim on the old path before rescanning, or the next save
        // would write the note straight back to the old filename.
        _currentRelativePath = null;
        AppSession.CurrentNoteRelativePath = null;

        RescanAndRefreshList();
        ShowAndEditNote(newName);
        StatusText.Text = $"Renamed {currentName} to {newName}.";
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
        EditorStatusText.Text = relativePath is null ? "Select a note to edit." : $"Editing {relativePath}";
        NoteTitleText.Text = relativePath is null
            ? "No note selected"
            : Path.GetFileNameWithoutExtension(relativePath);

        // Deferred to the next dispatcher cycle - setting Editor.Text synchronously
        // inline in the click/scan call stack was part of what reproduced the crash
        // described above.
        DispatcherQueue.TryEnqueue(() => Editor.Text = content);

        ShowNoteMetadata(relativePath, content);
        ShowBacklinks(relativePath);
        ShowWordCount(content);

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
