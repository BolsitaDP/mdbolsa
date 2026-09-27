using Microsoft.UI;
using Windows.UI;

namespace MdBolsa_Desktop_WinUI;

// The colours both visual representations share.
//
// ADR 0009 left this as a note to self: node colours were hardcoded in the graph
// page, and when a *second* representation appeared they should move somewhere
// both could read. Diagrams are that second representation, so here they are.
//
// What is deliberately not here: anything about size, position or shape. Those are
// per-view - the graph is a force layout of a whole vault, a diagram is a ranked
// layout of a few boxes - and pretending otherwise would be a false abstraction.
// Only the palette is shared, because "a note is blue" is a fact about the app,
// not about either view.
internal static class Presentation
{
    // Nodes.
    public static Color NoteNode { get; } = Colors.SteelBlue;
    public static Color TagNode { get; } = Colors.MediumPurple;
    public static Color CenterNode { get; } = Colors.Goldenrod;
    public static Color DiagramNode { get; } = Color.FromArgb(255, 62, 84, 110);
    public static Color DiagramDecision { get; } = Color.FromArgb(255, 92, 72, 58);
    public static Color DiagramStart { get; } = Color.FromArgb(255, 52, 88, 74);

    // Edges. Softer than the node colours on purpose: a line behind text should be
    // findable, not loud.
    public static Color LinkEdge { get; } = Color.FromArgb(150, 110, 160, 220);
    public static Color TagEdge { get; } = Color.FromArgb(150, 160, 120, 210);

    public static Color NodeBorder { get; } = Color.FromArgb(210, 200, 214, 230);
    public static Color EdgeLabelBackground { get; } = Color.FromArgb(230, 24, 26, 30);
    public static Color SubgraphBorder { get; } = Color.FromArgb(120, 150, 158, 175);
    public static Color Warning { get; } = Color.FromArgb(220, 214, 168, 84);
}
