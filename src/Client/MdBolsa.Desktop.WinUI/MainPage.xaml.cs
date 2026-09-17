using MdBolsa.Core.Vault;
using MdBolsa.Data.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdBolsa_Desktop_WinUI;

// Phase 3 editor: open a vault, browse its notes, and view a note's content. Intended
// to also save edits (SaveButton/OnSaveClicked), but as of this commit that path is
// UNVERIFIED and reproducibly crashes in manual testing - see the KNOWN ISSUE note in
// docs/architecture.md before relying on it. Loading/viewing a note (Editor.Text SET,
// deferred below) is solid; reading it back (Editor.Text GET, in SaveCurrentNote) is
// not, on this WindowsAppSDK 2.4.0 (preview) build. Tried: TextChanged handlers (even
// empty ones), polling timers, GetValue(TextBox.TextProperty) instead of .Text,
// RichEditBox.Document instead of TextBox - all still crash natively on reading the
// control's content back after it's been set programmatically. No wiki-links/
// backlinks/tags/search yet either (later phases) - this is meant to be the core
// open/edit/save loop, minus a working save.
public sealed partial class MainPage : Page
{
    private string? _vaultPath;
    private string? _currentRelativePath;

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
        OpenNoteInEditor(fileName);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e) => SaveCurrentNote();

    private void OpenNoteInEditor(string? relativePath)
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
        // inline in the click/scan call stack is part of what reproduced the native
        // crash described above.
        DispatcherQueue.TryEnqueue(() => Editor.Text = content);
    }

    private void SaveCurrentNote()
    {
        if (_currentRelativePath is null || _vaultPath is null) return;

        try
        {
            var text = NormalizeLineEndings(Editor.Text);
            File.WriteAllText(ResolvePath(_currentRelativePath), text);
            new VaultScanner(_vaultPath, OpenIndex()).Scan();
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

    private void RescanAndRefreshList()
    {
        var index = OpenIndex();
        var result = new VaultScanner(_vaultPath!, index).Scan();

        StatusText.Text = $"Added {result.Added}, updated {result.Updated}, moved {result.Moved}, " +
                           $"deleted {result.Deleted}, unchanged {result.Unchanged}.";

        // Plain Buttons in a StackPanel, not a ListView - a ListView (with or without
        // SelectionChanged/ItemClick, with or without a handler that does anything) was
        // never actually the problem; the Editor TextBox/timer combination described
        // above was. Kept as Buttons since they're already proven stable here and are
        // plenty for personal-vault scale; revisit if a later phase needs
        // virtualization for very large vaults.
        NotesList.Children.Clear();
        foreach (var note in index.GetAll().OrderBy(n => n.RelativePath))
        {
            var relativePath = note.RelativePath;
            var button = new Button
            {
                Content = relativePath,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => OpenNoteInEditor(relativePath);
            NotesList.Children.Add(button);
        }
    }

    private string ResolvePath(string relativePath) =>
        Path.Combine(_vaultPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static SqliteVaultIndex OpenIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));
}
