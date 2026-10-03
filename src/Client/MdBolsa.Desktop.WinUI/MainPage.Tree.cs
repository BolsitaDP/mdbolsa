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

// The sidebar's folder tree and inline rename. Part of MainPage; see MainPage.xaml.cs
// for the class-wide notes, and docs/architecture.md's Known Issues before touching it.
public sealed partial class MainPage
{
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
        _switcherMode = false;
        _flatResults = [];
        SearchBox.Text = string.Empty;
        SearchBox.PlaceholderText = "Search notes (Ctrl+F)";
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
        ScheduleSyncAfterWrite();
    }
}
