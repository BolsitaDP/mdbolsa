using MdBolsa.Core.Graph;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
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

    // "tag:" - the same prefix Core builds tag keys with. It is private there on
    // purpose (nothing outside the graph should be constructing node keys), and this
    // is the one place the shell needs to recognise one.
    private const string TagKeyPrefix = "tag:";

    // Edge colours, slightly softer than the pure theme colours: a line behind text
    // should be findable, not loud.
    private static readonly Color LinkColour = Color.FromArgb(150, 110, 160, 220);
    private static readonly Color TagColour = Color.FromArgb(150, 160, 120, 210);

    // WinUI's transforms take floats; the layout maths in Core is all doubles,
    // so the casts are the boundary between the two.
    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translateTransform = new();

    private GraphModel _graph = GraphModel.Empty;
    private Dictionary<string, (double X, double Y)> _positionsByKey = new();
    private Dictionary<string, Guid> _noteIdsByPath = new();

    // --- Drawing, dragging and hover ---------------------------------------
    //
    // A node is a circle *and* a label, and both have to move together, so they are
    // kept as one record rather than as two dictionaries that can disagree. An edge
    // keeps its endpoint *keys* so it can be re-pointed when a node is dragged:
    // without that, dragging a node leaves its lines behind, which is the single
    // ugliest thing a force graph can do.
    private sealed record NodeVisual(
        Ellipse Circle,
        Border Label,
        double Diameter,
        bool IsTag,
        ScaleTransform Scale);

    private sealed record EdgeVisual(Line Line, string SourceKey, string TargetKey);

    private readonly Dictionary<string, NodeVisual> _visualsByKey = new(StringComparer.Ordinal);
    private readonly List<EdgeVisual> _edgeVisuals = [];
    private readonly Dictionary<string, HashSet<string>> _adjacency = new(StringComparer.Ordinal);

    // Where a node was dropped. Pinned nodes are re-applied after every layout pass,
    // so an arrangement somebody made by hand survives a refresh - the behaviour
    // Obsidian has, and the reason dragging feels like editing rather than fighting
    // the algorithm. "Reset view" unpins everything, and says so on the button.
    private readonly Dictionary<string, (double X, double Y)> _pinnedByKey = new(StringComparer.Ordinal);

    // One gesture at a time: a drag is either a node drag or a canvas pan, never
    // both, and the mode is decided on press.
    private string? _dragKey;
    private bool _draggingNode;
    private bool _panning;
    private double _dragGrabOffsetX;
    private double _dragGrabOffsetY;
    private double _lastPointerX;
    private double _lastPointerY;
    private string? _hoverKey;

    // Click versus double click, without a Tapped event that fires twice on Windows
    // (which would open every note on a single click). The node's own Tapped handler
    // compares timestamps instead.
    private string? _pressedKey;
    private DateTimeOffset _pressedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(400);
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
        // Reset means reset: the view is re-fitted *and* every node somebody dragged
        // by hand goes back to where the layout puts it. Leaving the pins in place
        // would make the button a lie, and there is nowhere else to unpins.
        _pinnedByKey.Clear();
        _viewTouchedByUser = false;

        Rebuild();
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
        _edgeVisuals.Clear();
        _adjacency.Clear();

        if (_graph.NodeCount == 0) return;

        var centerKey = _centerNoteId is null
            ? null
            : GraphNode.ForNote(_centerNoteId.Value, string.Empty).Key;

        // Adjacency first: hover highlighting needs the neighbourhood, and building
        // it here means it is never out of step with the edges being drawn.
        foreach (var edge in _graph.Edges)
        {
            Link(edge.SourceKey, edge.TargetKey);
            Link(edge.TargetKey, edge.SourceKey);
        }

        // Edges before nodes, so circles and labels draw on top of the lines.
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
                Stroke = new SolidColorBrush(isTagEdge ? TagColour : LinkColour),
                StrokeThickness = isTagEdge ? 1 : 1.25,
                Opacity = isTagEdge ? 0.22 : 0.35,
            };

            if (isTagEdge) line.StrokeDashArray = new DoubleCollection { 3, 4 };

            GraphCanvas.Children.Add(line);
            _edgeVisuals.Add(new EdgeVisual(line, edge.SourceKey, edge.TargetKey));
        }

        foreach (var node in _graph.Nodes)
        {
            if (!_positionsByKey.TryGetValue(node.Key, out var position)) continue;

            var isCenter = node.Key == centerKey;

            // Tag nodes are smaller, as in Obsidian: a tag is a property of a note, not
            // a thing in its own right, and drawing it at note size overstates it.
            var isTag = node.Kind == GraphNodeKind.Tag;
            var diameter = isCenter ? NodeDiameter * 1.3 : isTag ? NodeDiameter * 0.6 : NodeDiameter;

            var fill = new SolidColorBrush(ColorFor(node, isCenter));
            var scale = new ScaleTransform { CenterX = diameter / 2, CenterY = diameter / 2 };

            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = fill,
                Stroke = new SolidColorBrush(Colors.Transparent),
                StrokeThickness = 2,
                RenderTransform = scale,
            };

            Canvas.SetLeft(ellipse, position.X - diameter / 2);
            Canvas.SetTop(ellipse, position.Y - diameter / 2);

            // The label sits in a rounded plate rather than bare text: over a dozen
            // crossing edges, unbacked text is unreadable exactly where the graph gets
            // interesting.
            var label = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 2),
                Background = new SolidColorBrush(Color.FromArgb(190, 32, 32, 32)),
                Child = new TextBlock
                {
                    Text = DisplayLabelFor(node),
                    Width = LabelWidth,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 11,
                    IsTextSelectionEnabled = false,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                // Taps belong to the node, not to its name.
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(label, position.X - LabelWidth / 2);
            Canvas.SetTop(label, position.Y + diameter / 2 + 3);

            var key = node.Key;

            ellipse.Tapped += (_, args) =>
            {
                // Obsidian opens the note on a click. Here a click selects, because the
                // bar underneath the graph is a real panel (path, tags, "make this the
                // centre") and losing it on every tap would be a worse trade. A double
                // click opens, and the bar says so.
                if (_pressedKey == key && DateTimeOffset.UtcNow - _pressedAt < DoubleClickWindow)
                {
                    OpenNoteFor(key);
                }
                else
                {
                    SelectNode(key);
                }

                args.Handled = true;
            };

            ellipse.PointerEntered += (_, _) => SetHover(key);
            ellipse.PointerExited += (_, _) => ClearHover(key);
            ellipse.PointerPressed += (_, args) =>
            {
                _pressedKey = key;
                _pressedAt = DateTimeOffset.UtcNow;

                BeginNodeDrag(key, args.GetCurrentPoint(GraphCanvas).Position);

                // The node captures the pointer itself. Without this the press is
                // marked handled, the canvas never sees it, nothing is captured, and
                // every move afterwards goes to whatever happens to be under the
                // cursor - which is a drag that does not drag.
                if (args.Pointer is not null)
                {
                    _capturedPointer = args.Pointer;
                    GraphCanvas.CapturePointer(args.Pointer);
                }

                args.Handled = true;
            };

            GraphCanvas.Children.Add(ellipse);
            GraphCanvas.Children.Add(label);

            _visualsByKey[key] = new NodeVisual(ellipse, label, diameter, isTag, scale);
        }

        if (_hoverKey is not null) ApplyHover(_hoverKey);
        if (_selectedKey is not null) HighlightSelection();
    }

    // There is deliberately **no fade-in on render**. It was here, it set every node
    // and label to Opacity 0 and animated to 1, and on this runtime the storyboard
    // did not always run - leaving the labels invisible and the graph looking blank.
    // A graph that renders nothing is the worst possible failure for this feature, and
    // an entrance animation is worth exactly nothing next to that. The lift and the
    // settle (below) animate a transform and always end on a valid value, so they
    // stayed.
    private void Link(string from, string to)
    {
        if (!_adjacency.TryGetValue(from, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _adjacency[from] = set;
        }

        set.Add(to);
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

    // A ring on the selected node. It deliberately does not touch Opacity, because
    // hover owns that: two features fading each other's nodes looks like a bug.
    private void HighlightSelection()
    {
        foreach (var pair in _visualsByKey)
        {
            var isSelected = pair.Key == _selectedKey;
            pair.Value.Circle.Stroke = new SolidColorBrush(isSelected ? Colors.White : Colors.Transparent);
            pair.Value.Circle.StrokeThickness = isSelected ? 2.5 : 2;
        }
    }

    private void ShowSelectionDetails()
    {
        if (_selectedKey is null || _graph.Find(_selectedKey) is not { } node)
        {
            SelectionText.Text = "Click a node to see it, double-click to open the note. " +
                "Drag the background or use the middle/right button to pan, the wheel to zoom, " +
                "shift+wheel or alt+wheel to scroll. Drag a node to move it - Reset view puts everything back.";
            CenterOnSelectionButton.IsEnabled = false;
            SelectionText.Text = "Click a node to see it, double-click to open the note. " +
                "Drag the background or use the middle/right button to pan, the wheel to zoom, " +
                "shift+wheel or alt+wheel to scroll. Drag a node to move it - Reset view puts everything back.";
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

    // --- Pointer: wheel, dragging, panning -------------------------------------

    // The wheel zooms, as it does in Obsidian, anchored on the pointer so whatever is
    // under the cursor stays under the cursor. Shift and Alt pan instead, which is
    // what everyone expects from a wheel and is the only way to move the graph
    // vertically on a machine with no scroll lock.
    // On the page rather than on the canvas. Pointer events bubble, so this sees the
    // wheel wherever it lands inside the graph - over a node, over a label, over the
    // background - instead of depending on which element happens to be the hit target
    // at that pixel.
    private void OnGraphPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        // Only over the graph itself. The page also holds the scope and depth
        // dropdowns, and a wheel over one of those should scroll it, not zoom.
        if (!IsOverCanvas(point.Position)) return;

        // A wheel notch is 120 "degrees"; the divisor is what makes one notch feel like
        // one notch instead of a jump.
        var notches = point.Properties.MouseWheelDelta / 120.0;
        if (Math.Abs(notches) < 0.0001) return;

        e.Handled = true;
        _viewTouchedByUser = true;

        var shift = _shiftDown;
        if (!shift && !_altDown)
        {
            ZoomAt(Math.Pow(1.15, notches), point.Position.X, point.Position.Y);
            return;
        }

        _translateTransform.X += (float)(shift ? notches * 120 : 0);
        _translateTransform.Y += (float)(shift ? 0 : notches * 120);
    }

    // Modifiers are tracked from the page's own key events rather than queried at the
    // wheel event. The querying API returns a flags enum that has to be compared
    // against a member whose namespace is not obvious from the documentation, and
    // this is simpler, has no projection to get wrong, and gives a place to clear a
    // stuck modifier when the window loses focus.
    private bool _shiftDown;
    private bool _altDown;

    private void OnModifierKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Shift: _shiftDown = true; break;
            case VirtualKey.Menu: _altDown = true; break;
        }
    }

    private void OnModifierKeyUp(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Shift: _shiftDown = false; break;
            case VirtualKey.Menu: _altDown = false; break;
        }
    }

    // Alt-tab away holding shift and come back: the KeyUp went to another window, and
    // without this the next wheel scroll pans when it should zoom.
    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _shiftDown = false;
        _altDown = false;
    }

    // Where the canvas sits inside the page, so a gesture over a dropdown is not
    // mistaken for a gesture over the graph.
    private bool IsOverCanvas(Windows.Foundation.Point pagePoint)
    {
        var origin = GraphCanvas.TransformToVisual(this);
        var offset = origin.TransformPoint(new Windows.Foundation.Point(0, 0));
        return pagePoint.X >= offset.X && pagePoint.X <= offset.X + GraphCanvas.ActualWidth
            && pagePoint.Y >= offset.Y && pagePoint.Y <= offset.Y + GraphCanvas.ActualHeight;
    }

    private void OnGraphPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!IsOverCanvas(point.Position)) return;
        var properties = point.Properties;

        _lastPointerX = point.Position.X;
        _lastPointerY = point.Position.Y;

        // The middle and right buttons pan, the way they do in Obsidian. The right
        // button is checked explicitly *and* taken away from the context menu, which
        // otherwise appears over the graph and eats the gesture.
        _panning = properties.IsRightButtonPressed || properties.IsMiddleButtonPressed;
        if (_panning && properties.IsRightButtonPressed)
        {
            GraphCanvas.ContextFlyout = null;
        }

        if (e.Pointer is not null && IsOverCanvas(point.Position))
        {
            _capturedPointer = e.Pointer;
            GraphCanvas.CapturePointer(e.Pointer);
        }
    }

    private void OnGraphPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(GraphCanvas);
        var x = point.Position.X;
        var y = point.Position.Y;

        if (_draggingNode && _dragKey is not null)
        {
            // Screen to virtual, minus where inside the node the pointer grabbed it -
            // otherwise the node jumps so its centre is under the cursor, which is
            // startling on the first pixel of movement.
            MoveNode(_dragKey, ScreenToVirtualX(x) - _dragGrabOffsetX, ScreenToVirtualY(y) - _dragGrabOffsetY);
            return;
        }

        if (_panning)
        {
            _translateTransform.X += (float)(x - _lastPointerX);
            _translateTransform.Y += (float)(y - _lastPointerY);
            _lastPointerX = x;
            _lastPointerY = y;
            _viewTouchedByUser = true;
        }
    }

    // Also the capture-lost handler: losing the capture mid-drag - a context menu, an
    // alt-tab, another window on top - must not leave a node stuck to the pointer.
    private void OnGraphPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndGesture();
    }

    private void EndGesture()
    {
        if (_draggingNode && _dragKey is not null)
        {
            // The settle: the node eases back to its resting size, which is what makes
            // a drag feel like picking something up rather than teleporting it.
            SettleNode(_dragKey);
        }

        _draggingNode = false;
        _panning = false;
        _dragKey = null;

        if (_capturedPointer is not null)
        {
            GraphCanvas.ReleasePointerCapture(_capturedPointer);
            _capturedPointer = null;
        }
    }

    private Pointer? _capturedPointer;

    private void BeginNodeDrag(string key, Windows.Foundation.Point screenPoint)
    {
        _dragKey = key;
        _draggingNode = true;
        _viewTouchedByUser = true;

        if (!_positionsByKey.TryGetValue(key, out var position)) return;

        // The grab offset is in virtual units, so it scales with the zoom: grabbing the
        // edge of a node at 0.3x must still not throw the node across the screen.
        _dragGrabOffsetX = ScreenToVirtualX(screenPoint.X) - position.X;
        _dragGrabOffsetY = ScreenToVirtualY(screenPoint.Y) - position.Y;

        LiftNode(key);
    }

    // Moves a node, its label and every edge that touches it.
    private void MoveNode(string key, double x, double y)
    {
        x = Math.Clamp(x, 0, VirtualWidth);
        y = Math.Clamp(y, 0, VirtualHeight);

        _positionsByKey[key] = (x, y);
        _pinnedByKey[key] = (x, y);

        if (!_visualsByKey.TryGetValue(key, out var visual)) return;

        Canvas.SetLeft(visual.Circle, x - visual.Diameter / 2);
        Canvas.SetTop(visual.Circle, y - visual.Diameter / 2);
        Canvas.SetLeft(visual.Label, x - LabelWidth / 2);
        Canvas.SetTop(visual.Label, y + visual.Diameter / 2 + 3);

        foreach (var edge in _edgeVisuals)
        {
            if (edge.SourceKey == key && _positionsByKey.TryGetValue(edge.TargetKey, out var target))
            {
                edge.Line.X1 = x;
                edge.Line.Y1 = y;
                edge.Line.X2 = target.X;
                edge.Line.Y2 = target.Y;
            }
            else if (edge.TargetKey == key && _positionsByKey.TryGetValue(edge.SourceKey, out var source))
            {
                edge.Line.X2 = x;
                edge.Line.Y2 = y;
                edge.Line.X1 = source.X;
                edge.Line.Y1 = source.Y;
            }
        }
    }

    // The lift and the settle. Two doubles on a transform, which is the safe kind of
    // thing to animate here - and they are what make a drag read as picking a node up
    // rather than teleporting it.
    private void AnimateNodeScale(ScaleTransform target, double to, int milliseconds)
    {
        var storyboard = new Storyboard();

        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        storyboard.Begin();
    }

    private void LiftNode(string key)
    {
        if (_visualsByKey.TryGetValue(key, out var visual)) AnimateNodeScale(visual.Scale, 1.3, 90);
    }

    private void SettleNode(string key)
    {
        if (_visualsByKey.TryGetValue(key, out var visual)) AnimateNodeScale(visual.Scale, 1, 240);
    }

    private double ScreenToVirtualX(double screenX) =>
        (screenX - _translateTransform.X) / Math.Max(_scaleTransform.ScaleX, 0.0001);

    private double ScreenToVirtualY(double screenY) =>
        (screenY - _translateTransform.Y) / Math.Max(_scaleTransform.ScaleY, 0.0001);

    // --- Hover --------------------------------------------------------------

    // Hovering a node lights it and its neighbours and fades everything else. This is
    // the single biggest thing that makes a graph feel alive rather than drawn, and it
    // is the reason to draw edges at all: without a way to see *which* edges belong to
    // a node, the lines are noise.
    private void SetHover(string key)
    {
        if (_hoverKey == key) return;

        _hoverKey = key;
        ApplyHover(key);
    }

    private void ClearHover(string key)
    {
        if (_hoverKey != key) return;

        _hoverKey = null;

        foreach (var visual in _visualsByKey.Values) visual.Circle.Opacity = 1;
        foreach (var visual in _visualsByKey.Values) visual.Label.Opacity = 1;
        foreach (var edge in _edgeVisuals) ResetEdgeOpacity(edge);

        if (_selectedKey is not null) HighlightSelection();
    }

    private void ApplyHover(string key)
    {
        var neighbours = _adjacency.TryGetValue(key, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);
        neighbours.Add(key);

        foreach (var pair in _visualsByKey)
        {
            var isNear = neighbours.Contains(pair.Key);
            var isSelected = pair.Key == _selectedKey;

            pair.Value.Circle.Opacity = isNear ? 1 : 0.18;
            pair.Value.Label.Opacity = isNear ? 1 : 0.12;
            pair.Value.Circle.Stroke = new SolidColorBrush(
                pair.Key == key ? Colors.White : Colors.Transparent);
        }

        foreach (var edge in _edgeVisuals)
        {
            var touches = edge.SourceKey == key || edge.TargetKey == key;
            var isTagEdge = edge.Line.StrokeDashArray is not null;

            edge.Line.Opacity = touches ? 0.9 : isTagEdge ? 0.05 : 0.08;
            edge.Line.StrokeThickness = touches ? 2 : 1.25;
        }
    }

    private static void ResetEdgeOpacity(EdgeVisual edge)
    {
        var isTagEdge = edge.Line.StrokeDashArray is not null;
        edge.Line.Opacity = isTagEdge ? 0.22 : 0.35;
        edge.Line.StrokeThickness = isTagEdge ? 1 : 1.25;
    }

    // Opens the note behind a node. A tag node has no note behind it, which is a
    // dead end rather than an error - Obsidian filters by tag on click, and that is
    // the honest version of the same idea.
    private void OpenNoteFor(string key)
    {
        if (key.StartsWith(TagKeyPrefix, StringComparison.Ordinal))
        {
            SelectionText.Text = $"Tag #{key[TagKeyPrefix.Length..]} - use the tags panel in the sidebar to filter by it.";
            return;
        }

        if (!GraphNode.TryGetNoteId(key, out var noteId)) return;
        if (AppSession.Host is not MainPage host) return;

        var relativePath = FindRelativePathForNote(noteId);
        if (relativePath is null) return;

        host.OpenNoteFromGraph(relativePath);
    }
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
