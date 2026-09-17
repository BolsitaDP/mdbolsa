using MdBolsa.Core.Links;
using MdBolsa.Core.Search;
using MdBolsa.Core.Vault;
using MdBolsa.Data.Links;
using MdBolsa.Data.Search;
using MdBolsa.Data.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdBolsa_Desktop_WinUI;

// Phase 3 editor + Phase 4 backlinks + Phase 5 search: open a vault, browse or search
// its notes, edit one as plain Markdown text, save it (explicitly via SaveButton, or
// implicitly when switching notes/vaults), and see its backlinks.
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
public sealed partial class MainPage : Page
{
    private string? _vaultPath;
    private string? _currentRelativePath;
    private Dictionary<Guid, NoteMetadata> _notesById = new();

    public MainPage()
    {
        InitializeComponent();
    }

    private void OnOpenVaultClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        _currentRelativePath = null;

        var path = VaultPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "Enter a valid vault folder path above, then click Open vault.";
            return;
        }

        _vaultPath = path;
        RescanAndRefreshList();
    }

    private void OnNewNoteClicked(object sender, RoutedEventArgs e)
    {
        if (_vaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        var fileName = "Untitled.md";
        var counter = 1;
        while (File.Exists(Path.Combine(_vaultPath, fileName)))
        {
            counter++;
            fileName = $"Untitled {counter}.md";
        }

        File.WriteAllText(Path.Combine(_vaultPath, fileName), "# Untitled\n");
        RescanAndRefreshList();
        ShowAndEditNote(fileName);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveCurrentNote();

    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        if (_vaultPath is null)
        {
            StatusText.Text = "Open a vault first.";
            return;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            PopulateNotesList(_notesById.Values.OrderBy(n => n.RelativePath));
            return;
        }

        var results = OpenSearchIndex().Search(query);
        StatusText.Text = $"{results.Count} search result(s) for \"{query}\".";

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
        if (_vaultPath is null) return;

        StatusText.Text = $"Showing all {_notesById.Count} note(s).";
        PopulateNotesList(_notesById.Values.OrderBy(n => n.RelativePath));
    }

    private void RescanAndRefreshList()
    {
        var vaultIndex = OpenVaultIndex();
        var vaultResult = new VaultScanner(_vaultPath!, vaultIndex).Scan();

        var linkIndex = OpenLinkIndex();
        var linkResult = new LinkScanner(_vaultPath!, vaultIndex, linkIndex).Scan();

        new SearchScanner(_vaultPath!, vaultIndex, OpenSearchIndex()).Scan();

        StatusText.Text = $"Notes: added {vaultResult.Added}, updated {vaultResult.Updated}, " +
                           $"moved {vaultResult.Moved}, deleted {vaultResult.Deleted}, " +
                           $"unchanged {vaultResult.Unchanged}. Links: {linkResult.Resolved} resolved, " +
                           $"{linkResult.Unresolved} unresolved.";

        var notes = vaultIndex.GetAll().OrderBy(n => n.RelativePath).ToList();
        _notesById = notes.ToDictionary(n => n.Id);
        PopulateNotesList(notes);
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

        EditorStatusText.Text = relativePath is null ? "Select a note to edit." : $"Editing {relativePath}";

        // Deferred to the next dispatcher cycle - setting Editor.Text synchronously
        // inline in the click/scan call stack was part of what reproduced the crash
        // described above.
        DispatcherQueue.TryEnqueue(() => Editor.Text = content);

        ShowBacklinks(relativePath);
    }

    private void ShowBacklinks(string? relativePath)
    {
        var note = relativePath is null ? null : _notesById.Values.FirstOrDefault(n => n.RelativePath == relativePath);
        var backlinkNotes = note is null
            ? []
            : OpenLinkIndex().GetBacklinkSourceIds(note.Id)
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
        if (_currentRelativePath is null || _vaultPath is null) return;

        try
        {
            var text = NormalizeLineEndings(Editor.Text);
            File.WriteAllText(ResolvePath(_currentRelativePath), text);

            var vaultIndex = OpenVaultIndex();
            new VaultScanner(_vaultPath, vaultIndex).Scan();
            new LinkScanner(_vaultPath, vaultIndex, OpenLinkIndex()).Scan();
            new SearchScanner(_vaultPath, vaultIndex, OpenSearchIndex()).Scan();

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
        Path.Combine(_vaultPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static SqliteVaultIndex OpenVaultIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));

    private static SqliteLinkIndex OpenLinkIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));

    private static SqliteSearchIndex OpenSearchIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));
}
