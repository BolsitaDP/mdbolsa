using MdBolsa.Core.Links;
using MdBolsa.Core.Vault;
using MdBolsa.Data.Links;
using MdBolsa.Data.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdBolsa_Desktop_WinUI;

// Phase 4: browse a vault's notes and see backlinks. Read-only by design - the Phase 3
// editor's Save path is unverified/known to crash on this machine (see feature/editor
// and docs/architecture.md), so this view only ever *sets* TextBlock.Text (proven safe
// throughout that investigation) and never reads a text-input control's content back.
public sealed partial class MainPage : Page
{
    private string? _vaultPath;
    private Dictionary<Guid, NoteMetadata> _notesById = new();

    public MainPage()
    {
        InitializeComponent();
    }

    private void OnOpenVaultClicked(object sender, RoutedEventArgs e)
    {
        var path = VaultPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "Enter a valid vault folder path above, then click Open vault.";
            return;
        }

        _vaultPath = path;
        RescanAndRefreshList();
    }

    private void RescanAndRefreshList()
    {
        var vaultIndex = OpenVaultIndex();
        var vaultResult = new VaultScanner(_vaultPath!, vaultIndex).Scan();

        var linkIndex = OpenLinkIndex();
        var linkResult = new LinkScanner(_vaultPath!, vaultIndex, linkIndex).Scan();

        StatusText.Text = $"Notes: added {vaultResult.Added}, updated {vaultResult.Updated}, " +
                           $"moved {vaultResult.Moved}, deleted {vaultResult.Deleted}, " +
                           $"unchanged {vaultResult.Unchanged}. Links: {linkResult.Resolved} resolved, " +
                           $"{linkResult.Unresolved} unresolved.";

        var notes = vaultIndex.GetAll().OrderBy(n => n.RelativePath).ToList();
        _notesById = notes.ToDictionary(n => n.Id);

        NotesList.Children.Clear();
        foreach (var note in notes)
        {
            var button = new Button
            {
                Content = note.RelativePath,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => ShowNote(note);
            NotesList.Children.Add(button);
        }
    }

    private void ShowNote(NoteMetadata note)
    {
        NoteTitleText.Text = note.RelativePath;

        string content;
        try
        {
            content = File.ReadAllText(ResolvePath(note.RelativePath));
        }
        catch (IOException)
        {
            NoteContentText.Text = "(could not read this note - it may have been moved or deleted)";
            content = string.Empty;
        }
        if (content.Length > 0) NoteContentText.Text = content;

        var backlinkNotes = OpenLinkIndex().GetBacklinkSourceIds(note.Id)
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
            button.Click += (_, _) => ShowNote(backlinkNote);
            BacklinksList.Children.Add(button);
        }
    }

    private string ResolvePath(string relativePath) =>
        Path.Combine(_vaultPath!, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static SqliteVaultIndex OpenVaultIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));

    private static SqliteLinkIndex OpenLinkIndex() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdBolsa.Dev", "index.db"));
}
