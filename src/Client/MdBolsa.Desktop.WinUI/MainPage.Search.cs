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

// Search, tags and the quick switcher. Part of MainPage; see MainPage.xaml.cs.
public sealed partial class MainPage
{
    // --- Search and tags ----------------------------------------------------

    // Search is applied on Enter, never on every keystroke. Live filtering means
    // rebuilding a list of controls from inside a TextChanged handler, and this
    // app's crash history (docs/architecture.md) makes anything that mutates
    // controls from a text input's own event a thing to avoid on purpose rather
    // than by luck.
    private void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        // If the box says something different from what the list on screen was built
        // from, this is a new search. If it says the same thing, a second click means
        // "open the top one" - which is the switcher's whole interaction, available
        // without a keyboard.
        //
        // Comparing the text is how this knows, rather than a flag set from
        // TextChanged: watching the box to drive the list is the pattern this app
        // deliberately does not use (see docs/architecture.md's Known Issues).
        if (_flatResults.Count > 0 && SearchBox.Text.Trim() == _activeQuery)
        {
            ShowAndEditNote(_flatResults[0].RelativePath);
            return;
        }

        RunSearch();
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;

                // Enter in the search box opens the top result, which is the whole
                // point of a switcher: type a few letters, press Enter, done. With
                // results on screen it is the only sensible thing Enter can mean,
                // and without them it falls back to running the search.
                if (_flatResults.Count > 0) ShowAndEditNote(_flatResults[0].RelativePath);
                else RunSearch();
                break;

            case VirtualKey.Escape:
                // Escape leaves the switcher and puts the file list back, rather than
                // clearing the box: you opened this to go somewhere, and the box is
                // where you were typing, not a dialog to dismiss.
                if (_switcherMode || SearchBox.Text.Length > 0)
                {
                    e.Handled = true;
                    OnClearSearchClicked(this, new RoutedEventArgs());
                }
                break;
        }
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
            if (_switcherMode) { ShowRecentNotes(); return; }
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

        // Names first, then the text index, and never the same note twice. A query
        // in a switcher is usually a name someone half-remembers; FTS alone would
        // rank a long note that happens to contain the word above the note that is
        // actually called it.
        var nameMatches = NoteNameMatcher.Match(_notesById.Values, query, limit: 15);
        var alreadyListed = new HashSet<Guid>(nameMatches.Select(match => match.Note.Id));

        var textMatches = AppSession.OpenSearchIndex().Search(query)
            .Select(result => _notesById.GetValueOrDefault(result.NoteId))
            .Where(note => note is not null && !alreadyListed.Contains(note.Id))
            .Select(note => note!)
            .ToList();

        var notes = nameMatches.Select(match => match.Note).Concat(textMatches).ToList();

        StatusText.Text = nameMatches.Count > 0
            ? $"{notes.Count} result(s) for \"{query}\" - {nameMatches.Count} by name."
            : $"{notes.Count} result(s) for \"{query}\"." +
              (clearedTag ? " Tag filter cleared." : string.Empty);

        // A name match says which folder it is in; a text match says why it matched.
        // One line, and which line is the useful one depends on how it was found.
        PopulateFlatList(
            notes,
            note => textMatches.Contains(note) ? SnippetFor(note, query) : FolderOf(note),
            $"SEARCH  \"{query}\"  ·  {notes.Count} match(es)");

        ShowSidebarView(FlatListPanel);
    }

    // "Personal/" - the folder, or nothing for a note at the vault root, which is
    // already obvious from where it sits in the tree.
    private static string? FolderOf(NoteMetadata note)
    {
        var slash = note.RelativePath.LastIndexOf('/');
        return slash < 0 ? null : note.RelativePath[..(slash + 1)];
    }

    private void ShowQuickSwitcher()
    {
        _switcherMode = true;

        // Ctrl+P is Obsidian's quick switcher. Folding the file list away would
        // hide the thing the switcher is replacing, so it opens the sidebar and puts
        // the cursor in the box.
        if (LeftSidebar.Visibility != Visibility.Visible) ToggleLeftSidebar();

        SearchBox.PlaceholderText = "Go to a note by name (Ctrl+P)";
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
        ShowRecentNotes();
    }

    private void ShowRecentNotes()
    {
        var notes = _recentlyOpened
            .Select(path => _notesById.Values.FirstOrDefault(note => note.RelativePath == path))
            .Where(note => note is not null)
            .Select(note => note!)
            .ToList();

        PopulateFlatList(notes, secondary: null, header: "RECENT");
        ShowSidebarView(FlatListPanel);
        StatusText.Text = notes.Count == 0
            ? "Type part of a note's name."
            : "Type part of a note's name, or pick one of these.";
    }

    private void RememberOpened(string relativePath)
    {
        _recentlyOpened.Remove(relativePath);
        _recentlyOpened.Insert(0, relativePath);
        if (_recentlyOpened.Count > 10) _recentlyOpened.RemoveAt(_recentlyOpened.Count - 1);
    }

    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        _switcherMode = false;
        _flatResults = [];
        SearchBox.Text = string.Empty;
        SearchBox.PlaceholderText = "Search notes (Ctrl+F)";
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

        PopulateFlatList(notes, secondary: null, header: $"#{_activeTag}  ·  {notes.Count} note(s)");
        ShowSidebarView(FlatListPanel);
        StatusText.Text = $"Filtering by #{_activeTag}. Click the tag again to clear.";
    }

    // One row list, used by search results, tag results and backlinks. Snippet is
    // the matched line for search and nothing for the others, so a result says why
    // it matched when there is a "why".
    private void PopulateFlatList(
        IReadOnlyList<NoteMetadata> notes, Func<NoteMetadata, string?>? secondary, string header)
    {
        _flatResults = notes;
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

            var secondaryText = secondary?.Invoke(note);
            if (!string.IsNullOrWhiteSpace(secondaryText))
            {
                content.Children.Add(new TextBlock
                {
                    Text = secondaryText,
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
}
