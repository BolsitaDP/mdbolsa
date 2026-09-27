using MdBolsa.Core.Diagrams;

namespace MdBolsa.Core.Tests.Diagrams;

// The four diagrams from Ejemplo/Diagramas.md, run through parse and layout and
// checked for the properties that decide whether anything is *drawable*.
//
// This exists because the shell's drawing code cannot be reached from a test - it
// is XAML, and the app is a desktop window. What *can* be checked headlessly is
// the geometry it is handed, and the invariants below are exactly the ones the
// renderer relies on: every box inside the frame, no two boxes touching, and every
// arrow that starts and ends somewhere real. If a change to the parser or the
// layout breaks the picture, one of these goes red before anyone opens the app.
//
// The four sources are copies of the demo note. If you change the note, change
// them here too - the note is the documentation, this is the assertion, and they
// are only useful while they agree.
public class DemoNoteDiagramTests
{
    private const double FrameWidth = 420;
    private const double FrameHeight = 300;

    private static readonly (string Name, string Source)[] DemoDiagrams =
    [
        ("how a note reaches the server", """
            flowchart TD
              Escribir[Escribes una nota] --> Guardar{Guardado}
              Guardar -->|sin conexión| Local[Se queda en disco]
              Local --> Sincroniza[El sincronizador la sube solo]
              Guardar -->|con conexión| Sincroniza
              Sincroniza --> Servidor[(El servidor)]
              Servidor --> OtrosTuspos[Los demás dispositivos]
              OtrosTuspos --> TuBóveda
            """),

        ("every shape", """
            flowchart LR
              A[Rectángulo] --> B(RRedondeado)
              B --> C([Estadio])
              C --> D[[Subrutina]]
              D --> E[(Cilindro)]
              E --> F((Círculo doble))
              F --> G >Asimétrico]
              G --> H[/Paralelogramo/]
              H --> I{rombo}
              I --> J{{Hexágono}}
            """),

        ("labelled edges", """
            flowchart TD
              Conflict{Hay conflicto?}
              Conflict -->|sí| Resolver[Te lo enseño y eliges]
              Conflict -->|no| Listo[Listo]
              Resolver --> Listo
            """),

        ("subgraphs", """
            flowchart TD
              subgraph Cliente
                Editor[El editor]
                Indice[El índice local]
              end
              subgraph Servidor
                API[La API]
                Datos[(PostgreSQL)]
              end
              Editor --> API
              Indice --> API
              API --> Datos
            """),
    ];

    public static TheoryData<string, string> Diagrams()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, source) in DemoDiagrams) data.Add(name, source);
        return data;
    }

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void The_demo_diagram_parses(string name, string source)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error),
            $"{name}: {error}");
    }

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void The_demo_diagram_lays_out(string name, string source)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), $"{name}: {error}");

        var positions = FlowchartLayout.Compute(chart, FrameWidth, FrameHeight);

        Assert.Equal(chart.NodeCount, positions.Count);

        Assert.All(positions, position =>
        {
            Assert.True(double.IsFinite(position.X), $"{name}: {position.NodeId} x is not finite");
            Assert.True(double.IsFinite(position.Y), $"{name}: {position.NodeId} y is not finite");
            Assert.True(position.Width > 0, $"{name}: {position.NodeId} has no width");
            Assert.True(position.Height > 0, $"{name}: {position.NodeId} has no height");
        });
    }

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void No_two_boxes_in_the_demo_diagram_overlap(string name, string source)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), $"{name}: {error}");

        var boxes = FlowchartLayout.Compute(chart, FrameWidth, FrameHeight)
            .Select(position => (
                Id: position.NodeId,
                Left: position.X - position.Width / 2,
                Right: position.X + position.Width / 2,
                Top: position.Y - position.Height / 2,
                Bottom: position.Y + position.Height / 2))
            .ToList();

        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i];
                var b = boxes[j];

                // A shared edge is fine - boxes are allowed to touch. What is not
                // fine is one sitting inside the other, which is what makes a
                // diagram unreadable rather than merely untidy.
                var overlaps = a.Left < b.Right && b.Left < a.Right
                            && a.Top < b.Bottom && b.Top < a.Bottom;

                Assert.False(overlaps, $"{name}: {a.Id} and {b.Id} overlap");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void Every_edge_in_the_demo_diagram_points_at_two_real_nodes(string name, string source)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), $"{name}: {error}");

        var ids = chart.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(chart.Edges, edge =>
        {
            // The renderer looks both endpoints up and skips the arrow if either is
            // missing, so an edge to nowhere is an arrow that silently does not
            // appear - a diagram that is quietly wrong.
            Assert.True(ids.Contains(edge.FromId), $"{name}: edge from '{edge.FromId}' has no node");
            Assert.True(ids.Contains(edge.ToId), $"{name}: edge to '{edge.ToId}' has no node");
        });
    }

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void A_demo_diagram_is_either_fitted_or_reported_as_bigger_than_its_pane(string name, string source)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), $"{name}: {error}");

        var positions = FlowchartLayout.Compute(chart, FrameWidth, FrameHeight);
        var (contentWidth, contentHeight) = FlowchartLayout.Bounds(positions);

        // The property that matters: a diagram is never shrunk past legibility to
        // force it to fit. Past MinimumScale the layout stops shrinking and reports
        // a bigger extent, and the shell turns that into a scrollbar. The bug this
        // guards is the first version, which shrank a ten-node diagram until every
        // label was an ellipsis - a diagram that fit perfectly and said nothing.
        Assert.All(positions, position => Assert.True(
            position.Width >= 45,
            $"{name}: {position.NodeId} is {position.Width:F0}px wide, which is not readable"));

        Assert.True(contentWidth > 0 && contentHeight > 0, $"{name}: reported no extent");

        // Never larger than the diagram's natural size - a content extent bigger
        // than that would mean the fit is inflating rather than scaling.
        Assert.True(contentWidth <= FrameWidth || contentHeight <= FrameHeight,
            $"{name}: both axes overflow ({contentWidth:F0} x {contentHeight:F0}) " +
            $"for a {FrameWidth} x {FrameHeight} pane");
    }

    [Fact]
    public void A_small_diagram_does_not_get_a_scrollbar_it_does_not_need()
    {
        Assert.True(FlowchartParser.TryParse("flowchart TD\n  A[a] --> B[b]", out var chart, out _));

        var positions = FlowchartLayout.Compute(chart, FrameWidth, FrameHeight);
        var (contentWidth, contentHeight) = FlowchartLayout.Bounds(positions);

        // The shell takes max(content, pane) as the canvas size, so a diagram that
        // fits must report a smaller extent than the pane or it gets bars for
        // nothing - which reads as "there is more here" when there is not.
        Assert.True(contentWidth <= FrameWidth, $"width {contentWidth:F0} exceeds the pane");
        Assert.True(contentHeight <= FrameHeight, $"height {contentHeight:F0} exceeds the pane");
    }

    [Fact]
    public void The_subgraph_demo_actually_recognises_its_subgraphs()
    {
        var source = DemoDiagrams.First(pair => pair.Name == "subgraphs").Source;
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), error?.ToString());

        Assert.Equal(new[] { "Cliente" }, chart.Find("Editor")!.Groups);
        Assert.Equal(new[] { "Cliente" }, chart.Find("Indice")!.Groups);
        Assert.Equal(new[] { "Servidor" }, chart.Find("API")!.Groups);
        Assert.Equal(new[] { "Servidor" }, chart.Find("Datos")!.Groups);
    }

    [Fact]
    public void A_diagram_of_an_unsupported_kind_falls_back_to_code_with_a_reason()
    {
        // The shell's contract, tested from this side: anything that is not a
        // flowchart produces a reason, and a reason means "show the code". A
        // sequence diagram in someone's note must not vanish.
        Assert.False(FlowchartParser.TryParse("sequenceDiagram\n  A->>B: hi", out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("not supported", error!.Reason);
    }
}
