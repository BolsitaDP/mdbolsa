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

// Note history and conflict resolution, the client side of Phase 10. Part of
// MainPage; see MainPage.xaml.cs.
public sealed partial class MainPage
{
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

        ScheduleSyncAfterWrite();
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

        ScheduleSyncAfterWrite();
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
}
