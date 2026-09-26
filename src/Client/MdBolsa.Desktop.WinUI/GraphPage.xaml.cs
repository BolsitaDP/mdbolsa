using MdBolsa.Core.Graph;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace MdBolsa_Desktop_WinUI;

// Phase 7's knowledge graph view (vision.md §12). Reads the same notes/links/tags
// indexes the other features use - it never reads a file itself - and renders
// them with a plain Canvas: a circle per node, a line per relationship, a
// read-only label under each node. Nodes and edges are `Ellipse`/`Line` children
// rather than anything fancier, for the same reason the notes list is Buttons:
// the proven-stable primitives first.
//
// The layout maths is not here. GraphLayout (in Core) computes the positions, so
// the algorithm is unit-testable and reusable by future clients; this page owns
// only presentation and input. The graph is laid out in a fixed virtual
// coordinate space (VirtualWidth x VirtualHeight) and mapped into the viewport by
// a scale/translate transform, so resizing the window doesn't relayout the
// graph - only the view transform changes.
//
// Every interaction is a click or a drag, never a TextChanged-style update, for
// the reason documented in MainPage's class comment.
public sealed partial class GraphPage : Page
{
    // The space the graph is laid out in, independent of the actual window size.
    private const double VirtualWidth = 1000;
    private const double VirtualHeight = 800;
    private const double NodeDiameter = GraphLayout.NodeRadius * 2;
    private const double MinScale = 0.15;
    private const double MaxScale = 2.5;
    private const double LabelWidth = 160;

    // WinUI's transforms take floats; the layout maths in Core is all doubles,
    // so the casts are the boundary between the two.
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();

    private GraphModel _graph = GraphModel.Empty;
    private Dictionary<string, (double X, double Y)> _positionsByKey = new();
    private Dictionary<string, FrameworkElement> _visualsByKey = new();
    private Dictionary<string, Guid> _noteIdsByPath = new();

    private string? _selectedKey;
    private Guid? _centerNoteId;
    private bool _viewTouchedByUser;
    private bool _suppressSelectionEvents;

    public GraphPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // The graph is laid out in a fixed virtual space and fitted to the viewport,
        // but on first display the Canvas hasn't been measured yet (ActualWidth is
        // 0), so the first fit falls back to the virtual size and leaves the graph
        // small in a corner. Re-fit once the real size is known - but never fight
        // the user: if they've panned or zoomed, leave the view alone.
        GraphCanvas.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width > 0 && !_viewTouchedByUser) FitViewToViewport();
        };
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        // Hand control back to the notes page, which owns showing/hiding this view.
        if (AppSession.Host is MainPage host) host.HideGraph();
    }

    // Everything here mutates controls - a RenderTransform, three combo boxes, and
    // a Canvas that ends up with a shape and a label per node - so all of it is
    // deferred to the next dispatcher cycle. Doing it inline in Loaded crashes the
    // process natively (STATUS_STOWED_EXCEPTION, 0xc000027b in Microsoft.UI.Xaml.dll):
    // mutating the tree while XAML is still processing Loaded is re-entrant, and the
    // resulting failure is stowed rather than thrown, so it can't be caught. Same
    // reason MainPage defers its auto-open and its Editor.Text. See
    // docs/architecture.md's Known Issues.
    // Everything here mutates controls - a RenderTransform, three combo boxes, and
    // a Canvas that ends up with a shape and a label per node - so it is deferred to
    // the next dispatcher cycle. Mutating the XAML tree while it is still processing
    // Loaded is re-entrant, and the failure is stowed rather than thrown, so it
    // crashes the process natively instead of raising a catchable exception. Same
    // reason MainPage defers its auto-open. See docs/architecture.md's Known Issues.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Populating the combo boxes fires their SelectionChanged handlers; the
            // flag keeps that from rebuilding the graph once per selection change
            // before there's anything to draw.
            _suppressSelectionEvents = true;

            // Scale first, then translate: the group's transform is
            // translate(scale(p)), so a pan moves the graph by exactly the pointer
            // delta regardless of zoom level.
            GraphCanvas.RenderTransform = new TransformGroup
            {
                Children = { _scaleTransform, _translateTransform },
            };

            DepthBox.Items.Clear();
            for (var depth = 1; depth <= GraphBuilder.MaxDepth; depth++)
            {
                DepthBox.Items.Add(depth.ToString());
            }

            PopulateCenterBox();
            DepthBox.SelectedIndex = 1;
            ScopeBox.SelectedIndex = 0;

            _suppressSelectionEvents = false;
            Rebuild();
        });
    }

    private void PopulateCenterBox()
    {
        CenterBox.Items.Clear();
        _noteIdsByPath = new Dictionary<string, Guid>(StringComparer.Ordinal);

        if (AppSession.VaultPath is null) return;

        var notes = AppSession.OpenVaultIndex().GetAll().OrderBy(n => n.RelativePath, StringComparer.Ordinal).ToList();
        foreach (var note in notes)
        {
            CenterBox.Items.Add(note.RelativePath);
            _noteIdsByPath[note.RelativePath] = note.Id;
        }

        // Default to whatever MainPage last had open, so "Note and neighbours"
        // starts where the user is rather than at an arbitrary note.
        var current = AppSession.CurrentNoteRelativePath;
        if (current is not null && _noteIdsByPath.ContainsKey(current)) CenterBox.SelectedItem = current;
        else if (CenterBox.Items.Count > 0) CenterBox.SelectedIndex = 0;
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        PopulateCenterBox();
        Rebuild();
    }

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => ZoomFromCenter(1.25);

    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => ZoomFromCenter(1 / 1.25);

    private void OnResetViewClicked(object sender, RoutedEventArgs e)
    {
        _viewTouchedByUser = false;
        FitViewToViewport();
    }

    private void ZoomFromCenter(double factor) => ZoomAt(factor, GraphCanvas.ActualWidth / 2, GraphCanvas.ActualHeight / 2);

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        var isLocal = ScopeBox.SelectedIndex == 1;
        CenterBox.IsEnabled = isLocal;
        DepthBox.IsEnabled = isLocal;
        if (_suppressSelectionEvents) return;
        Rebuild();
    }

    private void OnCenterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CenterBox.SelectedItem is string relativePath &&
            _noteIdsByPath.TryGetValue(relativePath, out var noteId))
        {
            _centerNoteId = noteId;
        }

        if (_suppressSelectionEvents) return;
        Rebuild();
    }

    private void OnDepthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents || DepthBox.SelectedIndex < 0) return;
        Rebuild();
    }

    private void OnCenterOnSelectionClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedKey is null || !GraphNode.TryGetNoteId(_selectedKey, out var noteId)) return;

        _centerNoteId = noteId;
        ScopeBox.SelectedIndex = 1;
        CenterBox.SelectedItem = FindRelativePathForNote(noteId);
        Rebuild();
    }

    private void Rebuild()
    {
        if (AppSession.VaultPath is null)
        {
            _graph = GraphModel.Empty;
            GraphStatusText.Text = "No vault open. Go back and open one first.";
            Render();
            return;
        }

        var builder = new GraphBuilder(
            AppSession.OpenVaultIndex(), AppSession.OpenLinkIndex(), AppSession.OpenTagIndex());

        var isLocal = ScopeBox.SelectedIndex == 1 && _centerNoteId is not null;
        if (isLocal)
        {
            var depth = DepthBox.SelectedIndex >= 0 ? DepthBox.SelectedIndex + 1 : 1;
            _graph = builder.BuildLocal(_centerNoteId!.Value, depth);
        }
        else
        {
            _graph = builder.BuildGlobal();
        }

        var positions = GraphLayout.Compute(_graph, VirtualWidth, VirtualHeight);
        _positionsByKey = positions.ToDictionary(p => p.NodeKey, p => (p.X, p.Y));

        GraphStatusText.Text = _graph.NodeCount switch
        {
            0 => "Nothing to show - this vault has no notes yet.",
            _ => $"{_graph.NodeCount} node(s), {_graph.EdgeCount} relationship(s). " +
                 $"Blue = note, purple = tag, gold = center note." +
                 (_graph.UnresolvedLinkCount > 0
                     ? $" {_graph.UnresolvedLinkCount} broken link(s) are not shown as nodes."
                     : string.Empty),
        };

        Render();
        if (!_viewTouchedByUser) FitViewToViewport();
    }

    private void Render()
    {
        GraphCanvas.Children.Clear();
        _visualsByKey.Clear();

        if (_graph.NodeCount == 0) return;

        // Edges first so nodes (and their labels) draw on top of the lines.
        foreach (var edge in _graph.Edges)
        {
            if (!_positionsByKey.TryGetValue(edge.SourceKey, out var from) ||
                !_positionsByKey.TryGetValue(edge.TargetKey, out var to))
            {
                continue;
            }

            var isTagEdge = edge.Kind == GraphEdgeKind.Tag;
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = new SolidColorBrush(isTagEdge ? Colors.MediumPurple : Colors.SteelBlue),
                StrokeThickness = isTagEdge ? 1 : 1.5,
                Opacity = isTagEdge ? 0.3 : 0.45,
            };

            if (isTagEdge) line.StrokeDashArray = new DoubleCollection { 3, 3 };
            GraphCanvas.Children.Add(line);
        }

        var centerKey = _centerNoteId is null ? null : GraphNode.ForNote(_centerNoteId.Value, string.Empty).Key;

        foreach (var node in _graph.Nodes)
        {
            if (!_positionsByKey.TryGetValue(node.Key, out var position)) continue;

            var isCenter = node.Key == centerKey;
            var diameter = isCenter ? NodeDiameter * 1.3 : NodeDiameter;

            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = new SolidColorBrush(ColorFor(node, isCenter)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = isCenter ? 2.5 : 1,
            };

            Canvas.SetLeft(ellipse, position.X - diameter / 2);
            Canvas.SetTop(ellipse, position.Y - diameter / 2);

            var key = node.Key;
            ellipse.Tapped += (_, args) =>
            {
                SelectNode(key);
                args.Handled = true;
            };

            GraphCanvas.Children.Add(ellipse);
            _visualsByKey[key] = ellipse;

            var label = new TextBlock
            {
                Text = DisplayLabelFor(node),
                Width = LabelWidth,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Opacity = 0.85,
                IsHitTestVisible = false, // taps belong to the node, not its label
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Canvas.SetLeft(label, position.X - LabelWidth / 2);
            Canvas.SetTop(label, position.Y + diameter / 2 + 2);
            GraphCanvas.Children.Add(label);
        }

        if (_selectedKey is not null) HighlightSelection();
    }

    private static Color ColorFor(GraphNode node, bool isCenter)
    {
        if (isCenter) return Colors.Goldenrod;
        return node.Kind == GraphNodeKind.Tag ? Colors.MediumPurple : Colors.SteelBlue;
    }

    // A note node shows its file name; the info bar below the graph shows the
    // full relative path, so two same-named notes in different folders stay
    // distinguishable without crowding the graph with full paths.
    private static string DisplayLabelFor(GraphNode node)
    {
        if (node.Kind == GraphNodeKind.Tag) return "#" + node.Label;

        var lastSlash = node.Label.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? node.Label[(lastSlash + 1)..] : node.Label;
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName[..^3] : fileName;
    }

    private void SelectNode(string key)
    {
        _selectedKey = key;
        HighlightSelection();
        ShowSelectionDetails();
    }

    private void HighlightSelection()
    {
        foreach (var pair in _visualsByKey)
        {
            if (pair.Value is not Ellipse ellipse) continue;

            var isSelected = pair.Key == _selectedKey;
            ellipse.Stroke = new SolidColorBrush(isSelected ? Colors.White : Colors.Transparent);
            ellipse.StrokeThickness = isSelected ? 3 : 0;
            ellipse.Opacity = _selectedKey is null || isSelected ? 1 : 0.75;
        }
    }

    private void ShowSelectionDetails()
    {
        if (_selectedKey is null || _graph.Find(_selectedKey) is not { } node)
        {
            SelectionText.Text = "Select a node to see what it is. Drag to pan, use the zoom buttons to zoom.";
            CenterOnSelectionButton.IsEnabled = false;
            return;
        }

        var lines = new List<string>
        {
            node.Kind == GraphNodeKind.Tag ? $"Tag #{node.Label}" : $"Note {node.Label}",
            $"{_graph.DegreeOf(node.Key)} relationship(s)",
        };

        if (GraphNode.TryGetNoteId(node.Key, out var noteId))
        {
            var tags = AppSession.OpenTagIndex().GetTagsForNote(noteId);
            if (tags.Count > 0) lines.Add("Tags: " + string.Join(", ", tags.Select(t => "#" + t)));
        }

        SelectionText.Text = string.Join(" - ", lines);
        CenterOnSelectionButton.IsEnabled = node.Kind == GraphNodeKind.Note && ScopeBox.SelectedIndex == 1;
    }

    private string? FindRelativePathForNote(Guid noteId) =>
        _noteIdsByPath.FirstOrDefault(pair => pair.Value == noteId).Key;

    // --- View transform -------------------------------------------------------

    private void FitViewToViewport()
    {
        var viewportWidth = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : VirtualWidth;
        var viewportHeight = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : VirtualHeight;

        var scale = Math.Clamp(
            Math.Min(viewportWidth / VirtualWidth, viewportHeight / VirtualHeight) * 0.95,
            MinScale,
            MaxScale);

        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
        _translateTransform.X = (float)((viewportWidth - VirtualWidth * scale) / 2);
        _translateTransform.Y = (float)((viewportHeight - VirtualHeight * scale) / 2);
    }

    private void OnCanvasManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        // Cumulative.Scale is a plain float (not a Vector), and is 1 for a pure pan.
        if (e.Cumulative.Scale != 1)
        {
            ZoomAt(e.Cumulative.Scale, GraphCanvas.ActualWidth / 2, GraphCanvas.ActualHeight / 2);
        }

        _translateTransform.X += (float)e.Delta.Translation.X;
        _translateTransform.Y += (float)e.Delta.Translation.Y;
        _viewTouchedByUser = true;
        e.Handled = true;
    }

    // Scales around a viewport point, keeping whatever is under that point under
    // it - the transform maps virtual (x, y) to screen (x * scale + offsetX).
    private void ZoomAt(double factor, double anchorX, double anchorY)
    {
        var oldScale = (double)_scaleTransform.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < double.Epsilon) return;

        var ratio = newScale / oldScale;
        _translateTransform.X = (float)(anchorX - (anchorX - _translateTransform.X) * ratio);
        _translateTransform.Y = (float)(anchorY - (anchorY - _translateTransform.Y) * ratio);
        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
    }
}
