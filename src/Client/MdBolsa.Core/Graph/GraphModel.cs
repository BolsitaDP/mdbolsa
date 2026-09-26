namespace MdBolsa.Core.Graph;

public sealed record GraphEdge(string SourceKey, string TargetKey, GraphEdgeKind Kind);

// A rendered-or-renderable slice of the vault's relationships. Pure data: no
// coordinates, no colours, no notion of a viewport - the client owns layout and
// presentation (see docs/decisions/0009-local-graph.md).
public sealed record GraphModel(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, int UnresolvedLinkCount)
{
    public static GraphModel Empty { get; } = new([], [], 0);

    public int NodeCount => Nodes.Count;

    public int EdgeCount => Edges.Count;

    public GraphNode? Find(string key) => Nodes.FirstOrDefault(n => n.Key == key);

    // Number of edges touching a node - what the UI shows as its degree.
    public int DegreeOf(string key) =>
        Edges.Count(e => e.SourceKey == key || e.TargetKey == key);
}
