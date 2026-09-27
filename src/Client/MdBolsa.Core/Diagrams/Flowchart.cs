namespace MdBolsa.Core.Diagrams;

// Diagrams, as parsed from a Mermaid fence and laid out, in Core rather than in
// the shell. The same reasoning as the knowledge graph (0009) and the Markdown
// renderer (0013): the parsing and the layout are the parts worth testing, and
// Phase 13's other clients need the same answers.
//
// What this is *not*: Mermaid. It is the subset of Mermaid that people actually
// put in notes - flowcharts - parsed by hand, with no JavaScript and no bundled
// library. Anything outside the subset comes back as a parse failure and the
// shell shows the code as code, which is the same honest degradation the Markdown
// preview uses: better a code block you can read than a diagram that is subtly
// wrong about what you wrote.

/// <summary>Which way the flow runs. Mermaid's TD/TB/BT/LR/RL.</summary>
public enum DiagramDirection
{
    TopDown,
    BottomUp,
    LeftRight,
    RightLeft,
}

/// <summary>The box a node is drawn in. Mermaid's shape vocabulary, as far as it is used.</summary>
public enum DiagramShape
{
    Rectangle,
    Rounded,
    Stadium,
    Subroutine,
    Cylinder,
    Circle,
    DoubleCircle,
    Diamond,
    Hexagon,
    Asymmetric,
    Parallelogram,
    ParallelogramAlt,
    Trapezoid,
    TrapezoidAlt,
}

/// <summary>How an edge is drawn. Mermaid's four line kinds.</summary>
public enum DiagramEdgeStyle
{
    Solid,
    Dotted,
    Thick,
    DottedThick,
}

public sealed record DiagramNode(
    string Id,
    string Label,
    DiagramShape Shape,
    IReadOnlyList<string> Groups)
{
    /// <summary>
    /// How wide this node wants to be, in characters. The layout needs it before
    /// anything is drawn, and measuring text needs a font, so this is an estimate
    /// and the shell draws the box to fit the real text afterwards.
    /// </summary>
    public int LabelWidth => Label.Length;
}

public sealed record DiagramEdge(
    string FromId,
    string ToId,
    string? Label,
    DiagramEdgeStyle Style);

/// <summary>
/// A parsed flowchart. No coordinates: <see cref="FlowchartLayout"/> turns this
/// into positions, and the shell owns everything visual. Same split as the graph.
/// </summary>
public sealed record Flowchart(
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    DiagramDirection Direction,
    string? Title,
    IReadOnlyList<string> Warnings)
{
    public static Flowchart Empty { get; } =
        new([], [], DiagramDirection.TopDown, null, []);

    public int NodeCount => Nodes.Count;

    public int EdgeCount => Edges.Count;

    public DiagramNode? Find(string id) => Nodes.FirstOrDefault(node => node.Id == id);
}

/// <summary>A node placed on the board.</summary>
public readonly record struct DiagramPosition(
    string NodeId,
    double X,
    double Y,
    double Width,
    double Height,
    int Rank);
