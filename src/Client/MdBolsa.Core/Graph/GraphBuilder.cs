using MdBolsa.Core.Links;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Graph;

// Turns the vault's three derived indexes (notes, links, tags) into a graph.
// Nothing here reads a file or touches SQLite: the scanners own that, and the
// graph is a pure function of what they indexed, so the same builder serves both
// the global graph and the note-local one.
//
// Note-local is built by expanding outward from a center note over the *global*
// graph rather than by re-deriving relationships at each depth - one code path,
// and the local graph is by construction a subset of the global one.
public sealed class GraphBuilder(IVaultIndex vaultIndex, ILinkIndex linkIndex, ITagIndex tagIndex)
{
    // Depth is capped so a densely linked vault can't ask for an unbounded
    // neighbourhood; 1 is "direct links, backlinks and tags", 2 adds their
    // neighbours, and so on.
    public const int MaxDepth = 5;

    public GraphModel BuildGlobal()
    {
        var notes = vaultIndex.GetAll()
            .OrderBy(n => n.RelativePath, StringComparer.Ordinal)
            .ToDictionary(n => n.Id);

        var nodes = new List<GraphNode>(notes.Count);
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in notes.Values)
        {
            nodes.Add(GraphNode.ForNote(note.Id, note.RelativePath));
            nodeKeys.Add(GraphNode.ForNote(note.Id, string.Empty).Key);
        }

        var edges = new List<GraphEdge>();
        var seenEdges = new HashSet<(string Source, string Target, GraphEdgeKind Kind)>();
        int unresolved = 0;

        foreach (var link in linkIndex.GetAll())
        {
            // An unresolved (or now-dangling) link has no node to attach to. It
            // isn't dropped silently - it's counted, so the UI can still say
            // "3 broken links" - but it isn't drawn as a node either.
            if (link.TargetNoteId is null ||
                link.TargetNoteId == link.SourceNoteId ||
                !notes.ContainsKey(link.SourceNoteId) ||
                !notes.ContainsKey(link.TargetNoteId.Value))
            {
                if (link.TargetNoteId != link.SourceNoteId) unresolved++;
                continue;
            }

            AddEdge(
                edges,
                seenEdges,
                GraphNode.ForNote(link.SourceNoteId, string.Empty).Key,
                GraphNode.ForNote(link.TargetNoteId.Value, string.Empty).Key,
                GraphEdgeKind.Link);
        }

        foreach (var note in notes.Values)
        {
            foreach (var tag in tagIndex.GetTagsForNote(note.Id))
            {
                var tagNode = GraphNode.ForTag(tag);
                if (nodeKeys.Add(tagNode.Key)) nodes.Add(tagNode);

                AddEdge(
                    edges,
                    seenEdges,
                    GraphNode.ForNote(note.Id, string.Empty).Key,
                    tagNode.Key,
                    GraphEdgeKind.Tag);
            }
        }

        return new GraphModel(Sort(nodes), Sort(edges), unresolved);
    }

    public GraphModel BuildLocal(Guid centerNoteId, int depth)
    {
        var global = BuildGlobal();
        var center = GraphNode.ForNote(centerNoteId, string.Empty).Key;
        if (global.Find(center) is null) return GraphModel.Empty with { UnresolvedLinkCount = global.UnresolvedLinkCount };

        var levels = Math.Clamp(depth, 1, MaxDepth);
        var reached = new HashSet<string>(StringComparer.Ordinal) { center };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { center };

        for (var level = 0; level < levels; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in frontier)
            {
                foreach (var neighbour in Neighbours(global, key))
                {
                    if (reached.Add(neighbour)) next.Add(neighbour);
                }
            }

            if (next.Count == 0) break;
            frontier = next;
        }

        // The induced subgraph: an edge survives only if both ends were reached.
        var nodes = global.Nodes.Where(n => reached.Contains(n.Key)).ToList();
        var edges = global.Edges
            .Where(e => reached.Contains(e.SourceKey) && reached.Contains(e.TargetKey))
            .ToList();

        return new GraphModel(nodes, edges, global.UnresolvedLinkCount);
    }

    // Relationships are undirected for traversal purposes: a note's local graph
    // shows what it links to *and* what links to it (i.e. its backlinks), which
    // is the whole point of the view.
    private static IEnumerable<string> Neighbours(GraphModel graph, string key) =>
        graph.Edges
            .Where(e => e.SourceKey == key || e.TargetKey == key)
            .Select(e => e.SourceKey == key ? e.TargetKey : e.SourceKey);

    private static void AddEdge(
        List<GraphEdge> edges,
        HashSet<(string Source, string Target, GraphEdgeKind Kind)> seen,
        string sourceKey,
        string targetKey,
        GraphEdgeKind kind)
    {
        // Two different [[link texts]] can resolve to the same target (and a note
        // can repeat a tag), and the graph wants one relationship, not one per
        // spelling of it.
        if (seen.Add((sourceKey, targetKey, kind)))
        {
            edges.Add(new GraphEdge(sourceKey, targetKey, kind));
        }
    }

    private static List<GraphNode> Sort(List<GraphNode> nodes) =>
        nodes.OrderBy(n => n.Kind).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();

    private static List<GraphEdge> Sort(List<GraphEdge> edges) =>
        edges.OrderBy(e => e.Kind).ThenBy(e => e.SourceKey, StringComparer.Ordinal)
            .ThenBy(e => e.TargetKey, StringComparer.Ordinal).ToList();
}
