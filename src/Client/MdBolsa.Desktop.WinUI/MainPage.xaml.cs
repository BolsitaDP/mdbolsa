using MdBolsa.Core.Links;
using MdBolsa.Core.Search;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }

    private void OnOpenVaultClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        _currentRelativePath = null;
        _activeTag = null;

        var path = VaultPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "Enter a valid vault folder path above, then click Open vault.";
            return;
        }

        AppSession.VaultPath = path;
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

    // Phase 7's graph view is a second page rather than another panel here: it's
    // a different view of the same indexes, and MainPage is already dense. The
    // save happens first so the graph reads fresh indexes, not pre-edit ones.
    private void OnGraphClicked(object sender, RoutedEventArgs e)
    {
        if (AppSession.VaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        SaveCurrentNote();
        AppSession.CurrentNoteRelativePath = _currentRelativePath;

        if (Window.Current.Content is Frame frame) frame.Navigate(typeof(GraphPage));
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
            NotesList.Children.Add(button);
        }
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
