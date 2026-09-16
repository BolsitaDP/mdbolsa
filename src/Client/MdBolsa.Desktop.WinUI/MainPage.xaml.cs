using MdBolsa.Core.Vault;
using MdBolsa.Data.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdBolsa_Desktop_WinUI;

// Phase 2 smoke test: scans a vault (path typed in above) and lists what the vault
// index found. Not the editor - this only proves Core's VaultScanner and Data's
// SqliteVaultIndex work together end to end.
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        var vaultPath = VaultPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
        {
            StatusText.Text = "Enter a valid vault folder path above, then click Rescan.";
            return;
        }

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MdBolsa.Dev", "index.db");
        var index = new SqliteVaultIndex(dbPath);
        var result = new VaultScanner(vaultPath, index).Scan();

        StatusText.Text = $"Added {result.Added}, updated {result.Updated}, moved {result.Moved}, " +
                           $"deleted {result.Deleted}, unchanged {result.Unchanged}.";

        NotesList.ItemsSource = index.GetAll()
            .OrderBy(n => n.RelativePath)
            .Select(n => n.RelativePath)
            .ToList();
    }
}
