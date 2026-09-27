using MdBolsa.Core.Diagrams;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace MdBolsa_Desktop_WinUI;

// Draws a parsed flowchart.
//
// Same split as everywhere else in this project: the layout is Core's, and this
// only turns positions into shapes. It is a plain class rather than a UserControl
// because the preview builds one of these per diagram and throws it away 400ms
// later, and a control template per throwaway is a cost with no benefit.
//
// Two things it does that are worth knowing about:
//
// * **It re-lays out on resize instead of rebuilding.** The preview pane is built
//   before it has been measured, so the first layout is done against a guess. When
//   the real width arrives the existing elements are *moved*, never removed and
//   re-added - this runtime has crashed before on panels that mutate their own
//   children, and there is no reason to find out again.
// * **A node's box is drawn to the size the layout chose.** Where the label does
//   not fit, it is trimmed with an ellipsis rather than allowed to spill over the
//   border. A label that overflows its shape is worse than a shortened one, because
//   the shape is what tells you the text has a boundary.
internal sealed class FlowchartPresenter
{
    private readonly Flowchart _chart;
    private readonly Canvas _canvas;
    private readonly Border _frame;

    private readonly List<EdgeVisual> _edges = [];
    private readonly List<GroupVisual> _groups = [];
    private readonly List<NodeVisual> _nodes = [];

    private sealed record EdgeVisual(Line Line, Path? Head, DiagramEdge Edge, string FromId, string ToId);
    private sealed record GroupVisual(Rectangle Border, string Group, IReadOnlyList<string> Members);

    // A caption is a label on a group boundary, not a group.
    private sealed record GroupCaption(TextBlock Label, string Group);
    private sealed record NodeVisual(FrameworkElement Box, TextBlock Label, string NodeId);

    /// <summary>The font a node's label is drawn at before any scaling.</summary>
    private const double BaseFontSize = 12;

    /// <summary>How the parser marks a subgraph's title node. Mirrors Core.</summary>
    private const string SubgraphCaptionPrefix = "sub:";

    private readonly List<GroupCaption> _captions = [];

    private FlowchartPresenter(
        Flowchart chart, Border frame, Canvas canvas, ScrollViewer scroller)
    {
        _chart = chart;
        _frame = frame;
        _canvas = canvas;
        _scroller = scroller;
    }

    private readonly ScrollViewer _scroller;

    /// <summary>
    /// Builds the visual tree for a flowchart. The returned element is a Border
    /// that resizes itself: it asks to be at least a readable height, and re-lays
    /// out when the pane gives it more (or less) width than it assumed.
    /// </summary>
    public static FrameworkElement Create(Flowchart chart)
    {
        var canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };

        // A diagram too big for the pane is scrolled, not shrunk to illegibility -
        // see FlowchartLayout.MinimumScale, which is the other half of that
        // decision. Auto on both axes, so a small diagram gets no bars at all.
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            IsTabStop = false,
            Content = canvas,
        };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Presentation.NodeBorder),
            Background = new SolidColorBrush(Color.FromArgb(120, 20, 22, 26)),
            // Tall enough for a small diagram to read, short enough that a note
            // with three of them is still a note and not a wall of boxes.
            MinHeight = 200,
            MaxHeight = 460,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = scroller,
        };

        var presenter = new FlowchartPresenter(chart, frame, canvas, scroller);
        presenter.Build();

        // The pane has no width yet when the preview is first built, so the layout
        // that ran inside Build() used a nominal one. Correct it once the real size
        // is known.
        //
        // Deliberately deferred to the next dispatcher turn. Setting a child's size
        // or position from inside a SizeChanged handler re-enters layout while the
        // tree is still being measured, and this runtime dies with a stowed native
        // exception (0xc000027b in Microsoft.UI.Xaml.dll) when that happens - the
        // same signature as every other crash in the Known Issues. Enqueueing puts
        // the work after the measure pass, which is where mutation is safe. The
        // width check keeps a resize that did not actually change the size from
        // re-laying out on every frame.
        frame.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0) return;

            var width = args.NewSize.Width;
            var height = args.NewSize.Height;

            if (Math.Abs(width - presenter._lastWidth) < 0.5 &&
                Math.Abs(height - presenter._lastHeight) < 0.5)
            {
                return;
            }

            frame.DispatcherQueue.TryEnqueue(() =>
            {
                presenter._lastWidth = width;
                presenter._lastHeight = height;
                presenter.Relayout(width, height);
            });
        };

        return frame;
    }

    private double _lastWidth;
    private double _lastHeight;

    private void Build()
    {
        // A subgraph's title is stored as a node with a "sub:" id, which is what the
        // parser produces and what its tests assert. Here it is *not* drawn as a
        // box: a caption floating beside the group it labels reads as another node
        // in the diagram, and the whole point of the dashed rectangle is that it
        // is a boundary, not a step. So the title becomes a label on the boundary.
        var captionFor = _chart.Nodes
            .Where(node => node.Id.StartsWith(SubgraphCaptionPrefix, StringComparison.Ordinal))
            .ToDictionary(node => node.Id[SubgraphCaptionPrefix.Length..], node => node.Label);

        foreach (var group in _chart.Nodes
                     .SelectMany(node => node.Groups.Select(g => (Group: g, Node: node.Id)))
                     .GroupBy(pair => pair.Group)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var rectangle = new Rectangle
            {
                Stroke = new SolidColorBrush(Presentation.SubgraphBorder),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 8,
                RadiusY = 8,
            };

            _canvas.Children.Add(rectangle);

            if (captionFor.TryGetValue(group.Key, out var caption))
            {
                var label = new TextBlock
                {
                    Text = caption,
                    FontSize = 10,
                    Opacity = 0.75,
                    IsTextSelectionEnabled = false,
                    Margin = new Thickness(0, 2, 0, 0),
                };

                _canvas.Children.Add(label);
                _captions.Add(new GroupCaption(label, group.Key));
            }

            _groups.Add(new GroupVisual(
                rectangle, group.Key, group.Select(pair => pair.Node).ToList()));
        }

        foreach (var edge in _chart.Edges)
        {
            var line = new Line
            {
                Stroke = new SolidColorBrush(Presentation.LinkEdge),
                StrokeThickness = edge.Style is DiagramEdgeStyle.Thick or DiagramEdgeStyle.DottedThick ? 2.4 : 1.4,
                Opacity = 0.85,
            };

            if (edge.Style is DiagramEdgeStyle.Dotted or DiagramEdgeStyle.DottedThick)
            {
                line.StrokeDashArray = new DoubleCollection { 3, 3 };
            }

            // Headless edges (`---`) get no arrowhead, exactly as Mermaid draws
            // them: a line that means "related" rather than "leads to".
            Path? head = null;
            if (edge.Style is not DiagramEdgeStyle.Solid or DiagramEdgeStyle.Dotted)
            {
            }
            else
            {
                head = new Path { Fill = new SolidColorBrush(Presentation.LinkEdge) };
            }

            _canvas.Children.Add(line);
            if (head is not null) _canvas.Children.Add(head);

            _edges.Add(new EdgeVisual(line, head, edge, edge.FromId, edge.ToId));
        }

        foreach (var node in _chart.Nodes)
        {
            // Captions were handled above, on the group boundary.
            if (node.Id.StartsWith(SubgraphCaptionPrefix, StringComparison.Ordinal)) continue;

            var box = BuildNode(node, out var label);
            _canvas.Children.Add(box);
            _nodes.Add(new NodeVisual(box, label, node.Id));
        }

        // A nominal width for the first pass. The SizeChanged above replaces it as
        // soon as the real one is known.
        Relayout(520, 300);
    }

    private FrameworkElement BuildNode(DiagramNode node, out TextBlock label)
    {
        label = new TextBlock
        {
            Text = node.Label,
            FontSize = BaseFontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };

        var fill = new SolidColorBrush(node.Shape is DiagramShape.Diamond or DiagramShape.Hexagon
            ? Presentation.DiagramDecision
            : node.Groups.Count > 0
                ? Presentation.DiagramStart
                : Presentation.DiagramNode);

        var stroke = new SolidColorBrush(Presentation.NodeBorder);

        return node.Shape switch
        {
            DiagramShape.Circle or DiagramShape.DoubleCircle => BuildEllipse(node.Shape, fill, stroke, label),
            DiagramShape.Diamond => BuildPolygon(
                new[] { (0.5, 0.0), (1.0, 0.5), (0.5, 1.0), (0.0, 0.5) }, fill, stroke, label),

            DiagramShape.Hexagon => BuildPolygon(
                new[] { (0.25, 0.0), (0.75, 0.0), (1.0, 0.5), (0.75, 1.0), (0.25, 1.0), (0.0, 0.5) },
                fill, stroke, label),

            DiagramShape.Parallelogram => BuildPolygon(
                new[] { (0.18, 0.0), (1.0, 0.0), (0.82, 1.0), (0.0, 1.0) }, fill, stroke, label),

            DiagramShape.ParallelogramAlt => BuildPolygon(
                new[] { (0.0, 0.0), (0.82, 0.0), (1.0, 1.0), (0.18, 1.0) }, fill, stroke, label),

            DiagramShape.Trapezoid => BuildPolygon(
                new[] { (0.22, 0.0), (0.78, 0.0), (1.0, 1.0), (0.0, 1.0) }, fill, stroke, label),

            DiagramShape.TrapezoidAlt => BuildPolygon(
                new[] { (0.0, 0.0), (1.0, 0.0), (0.78, 1.0), (0.22, 1.0) }, fill, stroke, label),

            DiagramShape.Asymmetric => BuildPolygon(
                new[] { (0.0, 0.0), (0.8, 0.0), (1.0, 0.5), (0.8, 1.0), (0.0, 1.0) }, fill, stroke, label),

            DiagramShape.Stadium => new Border
            {
                CornerRadius = new CornerRadius(40),
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1.2),
                Child = label,
            },

            DiagramShape.Cylinder => BuildCylinder(fill, stroke, label),

            _ => new Border
            {
                CornerRadius = node.Shape == DiagramShape.Rounded ? new CornerRadius(10) : new CornerRadius(2),
                Background = fill,
                BorderBrush = node.Shape == DiagramShape.Subroutine
                    ? new SolidColorBrush(Presentation.NodeBorder)
                    : stroke,
                BorderThickness = new Thickness(node.Shape == DiagramShape.Subroutine ? 1.2 : 1),
                Child = label,
            },
        };
    }

    /// <summary>
    /// A cylinder is a rectangle with a lid. The lid is a separate ellipse rather
    /// than a border style because no border style draws one, and a rounded
    /// rectangle reads as a pill, not as storage.
    /// </summary>
    private static FrameworkElement BuildCylinder(Brush fill, Brush stroke, UIElement content)
    {
        var grid = new Grid
        {
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1.2),
        };

        grid.Children.Add(new Ellipse
        {
            Height = 12,
            Stroke = new SolidColorBrush(Presentation.NodeBorder),
            StrokeThickness = 1.2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1),
        });

        grid.Children.Add(content);
        return grid;
    }

    private static FrameworkElement BuildEllipse(
        DiagramShape shape, Brush fill, Brush stroke, UIElement content)
    {
        var grid = new Grid { Background = fill };

        if (shape == DiagramShape.DoubleCircle)
        {
            grid.Children.Add(new Grid
            {
                Margin = new Thickness(5),
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(60),
            });
        }

        grid.Children.Add(content);
        return grid;
    }

    private static FrameworkElement BuildPolygon(
        (double X, double Y)[] points, Brush fill, Brush stroke, UIElement content)
    {
        // A Grid whose first child is a Path stretched to fill it, with the label on
        // top. A Grid with a stretched child has a size to give the path, which is
        // what lets the geometry be described in 0..1 units instead of pixels - so
        // the same shape works at any size the layout chose.
        var grid = new Grid();

        grid.Children.Add(new Path
        {
            Stretch = Stretch.Fill,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.2,
            Data = BuildGeometry(points),
        });

        grid.Children.Add(content);
        return grid;
    }

    private static Geometry BuildGeometry((double X, double Y)[] points)
    {
        // Rendered into a 100x100 box and stretched, so the numbers stay readable
        // and the geometry is resolution independent.
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(points[0].X * 100, points[0].Y * 100), IsClosed = true, IsFilled = true };

        for (var i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(points[i].X * 100, points[i].Y * 100),
            });
        }

        figure.Segments.Add(new LineSegment
        {
            Point = new Windows.Foundation.Point(points[0].X * 100, points[0].Y * 100),
        });

        geometry.Figures.Add(figure);
        return geometry;
    }

    // --- Layout ------------------------------------------------------------

    private void Relayout(double width, double height)
    {
        var positions = FlowchartLayout.Compute(_chart, width, height, out var scale)
            .ToDictionary(p => p.NodeId);

        if (positions.Count == 0) return;

        // The font scales with the diagram. Without this the boxes shrink and the
        // text does not, and a box that fits eight characters of a seventeen
        // character label shows "Escribe..." - which is the diagram failing to say
        // the thing it is there to say. Core has no font, so it reports the scale
        // and the shell applies it.
        var fontSize = Math.Max(BaseFontSize * scale, 9);

        // The label's own padding scales too, for the same reason the font does. It
        // is 8px a side unscaled, and 16px of a 46px box is a third of it - at
        // small sizes that padding is the difference between the whole word and an
        // ellipsis.
        var labelMargin = new Thickness(8 * scale, 0, 8 * scale, 0);

        foreach (var node in _nodes)
        {
            if (!positions.TryGetValue(node.NodeId, out var position)) continue;

            node.Box.Width = position.Width;
            node.Box.Height = position.Height;
            node.Label.FontSize = fontSize;
            node.Label.Margin = labelMargin;

            Canvas.SetLeft(node.Box, position.X - position.Width / 2);
            Canvas.SetTop(node.Box, position.Y - position.Height / 2);
        }

        foreach (var edge in _edges)
        {
            if (!positions.TryGetValue(edge.FromId, out var from) ||
                !positions.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            // Stop the line at the edge of each box rather than at its centre, so
            // the arrowhead touches the shape instead of disappearing under it.
            var (x1, y1, x2, y2) = Trim(from, to);

            edge.Line.X1 = x1;
            edge.Line.Y1 = y1;
            edge.Line.X2 = x2;
            edge.Line.Y2 = y2;

            if (edge.Head is null) continue;

            var angle = Math.Atan2(y2 - y1, x2 - x1);
            PlaceHead(edge.Head, x2, y2, angle);
        }

        // Subgroup boxes are sized to the nodes inside them, so a group drawn
        // before the layout existed still hugs its contents.
        foreach (var group in _groups)
        {
            var members = group.Members.Where(positions.ContainsKey).Select(id => positions[id]).ToList();
            if (members.Count == 0)
            {
                group.Border.Visibility = Visibility.Collapsed;
                continue;
            }

            var left = members.Min(m => m.X - m.Width / 2);
            var right = members.Max(m => m.X + m.Width / 2);
            var top = members.Min(m => m.Y - m.Height / 2);
            var bottom = members.Max(m => m.Y + m.Height / 2);

            Canvas.SetLeft(group.Border, left - 14);
            Canvas.SetTop(group.Border, top - 14);
            group.Border.Width = right - left + 28;
            group.Border.Height = bottom - top + 28;
        }

        // The caption sits inside the top-left corner of its group, and scales with
        // the diagram for the same reason the node labels do.
        foreach (var caption in _captions)
        {
            var group = _groups.FirstOrDefault(candidate => candidate.Group == caption.Group);
            if (group is null || group.Members.Count == 0) continue;

            caption.Label.FontSize = Math.Max(10 * scale, 8);
            Canvas.SetLeft(caption.Label, Canvas.GetLeft(group.Border) + 8);
            Canvas.SetTop(caption.Label, Canvas.GetTop(group.Border) - 6);
        }

        // Give the canvas the size of what is on it, so the ScrollViewer knows
        // there is something to scroll to. Without an explicit size a Canvas
        // reports no extent at all and the scrollbars never appear - which would
        // make MinimumScale silently hide part of the diagram.
        var (contentWidth, contentHeight) = FlowchartLayout.Bounds(positions.Values.ToList());
        _canvas.Width = Math.Max(contentWidth, width);
        _canvas.Height = Math.Max(contentHeight, height);
    }

    private static (double X1, double Y1, double X2, double Y2) Trim(
        DiagramPosition from, DiagramPosition to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return (from.X, from.Y, to.X, to.Y);

        var ux = dx / length;
        var uy = dy / length;

        // How far to travel before leaving each box along the line. For a rectangle
        // this is the smaller of the two axis distances, which is close enough and
        // far simpler than the exact intersection - and being a couple of pixels
        // off is invisible, while a line crossing the label is not.
        var start = BoxExit(from, ux, uy);
        var end = BoxExit(to, -ux, -uy);

        return (
            from.X + ux * start,
            from.Y + uy * start,
            to.X - ux * end,
            to.Y - uy * end);
    }

    private static double BoxExit(DiagramPosition box, double ux, double uy)
    {
        var halfWidth = box.Width / 2;
        var halfHeight = box.Height / 2;

        var byWidth = Math.Abs(ux) < 0.0001 ? double.MaxValue : halfWidth / Math.Abs(ux);
        var byHeight = Math.Abs(uy) < 0.0001 ? double.MaxValue : halfHeight / Math.Abs(uy);

        return Math.Min(byWidth, byHeight);
    }

    private static void PlaceHead(Path head, double x, double y, double angle)
    {
        // A triangle pointing along +x, then rotated onto the line. Building the
        // geometry rotated costs nothing and avoids a second set of point tables.
        const double size = 8;
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(0, -size / 2),
            IsClosed = true,
            IsFilled = true,
        };

        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(size * 1.3, 0) });
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, size / 2) });
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(0, -size / 2) });
        geometry.Figures.Add(figure);

        head.Data = geometry;
        head.RenderTransform = new RotateTransform { Angle = angle * 180 / Math.PI };

        Canvas.SetLeft(head, x);
        Canvas.SetTop(head, y);
    }
}
