using MdBolsa.Core.Graph;

namespace MdBolsa.Core.Tests.Graph;

public class GraphLayoutTests
{
    [Fact]
    public void Compute_ReturnsNothing_ForAnEmptyGraph() => Assert.Empty(GraphLayout.Compute(GraphModel.Empty, 800, 600));

    [Fact]
    public void Compute_CentersASingleNode()
    {
        var graph = new GraphModel([GraphNode.ForNote(Guid.NewGuid(), "Only.md")], [], 0);

        var position = Assert.Single(GraphLayout.Compute(graph, 800, 600));

        Assert.Equal(400, position.X, precision: 6);
        Assert.Equal(300, position.Y, precision: 6);
    }

    [Fact]
    public void Compute_KeepsEveryNodeInsideTheViewport()
    {
        var graph = Chain(12);

        var positions = GraphLayout.Compute(graph, 640, 480);

        Assert.Equal(12, positions.Count);
        Assert.All(positions, p =>
        {
            Assert.InRange(p.X, GraphLayout.NodeRadius, 640 - GraphLayout.NodeRadius);
            Assert.InRange(p.Y, GraphLayout.NodeRadius, 480 - GraphLayout.NodeRadius);
        });
    }

    [Fact]
    public void Compute_SeparatesLinkedNodes()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var graph = new GraphModel(
            [GraphNode.ForNote(a, "A.md"), GraphNode.ForNote(b, "B.md")],
            [new GraphEdge(GraphNode.ForNote(a, "").Key, GraphNode.ForNote(b, "").Key, GraphEdgeKind.Link)],
            0);

        var positions = GraphLayout.Compute(graph, 800, 600);

        Assert.Equal(2, positions.Count);
        Assert.True(Distance(positions[0], positions[1]) > 1, "Linked nodes must not collapse onto each other.");
    }

    [Fact]
    public void Compute_SeparatesIsolatedNodes()
    {
        // No edges at all: the repulsion term alone still has to keep them apart.
        var nodes = Enumerable.Range(0, 6).Select(_ => GraphNode.ForNote(Guid.NewGuid(), "Note.md")).ToList();

        var positions = GraphLayout.Compute(new GraphModel(nodes, [], 0), 800, 600);

        Assert.Equal(6, positions.Count);
        Assert.Equal(6, positions.Select(p => (p.X, p.Y)).Distinct().Count());
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        // Same graph in, same pixels out - no RNG anywhere in the layout, so the
        // view doesn't reshuffle itself on every rebuild.
        var graph = Chain(10);

        var first = GraphLayout.Compute(graph, 800, 600);
        var second = GraphLayout.Compute(graph, 800, 600);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_UsesTheSameOrderAsTheGraphNodes()
    {
        var graph = Chain(6);

        var positions = GraphLayout.Compute(graph, 800, 600);

        Assert.Equal(graph.Nodes.Select(n => n.Key), positions.Select(p => p.NodeKey));
    }

    [Fact]
    public void Compute_HandlesDisconnectedComponentsWithoutThrowing()
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        for (var component = 0; component < 3; component++)
        {
            var previous = GraphNode.ForNote(Guid.NewGuid(), $"C{component}-0.md");
            nodes.Add(previous);
            for (var i = 1; i < 4; i++)
            {
                var node = GraphNode.ForNote(Guid.NewGuid(), $"C{component}-{i}.md");
                nodes.Add(node);
                edges.Add(new GraphEdge(previous.Key, node.Key, GraphEdgeKind.Link));
                previous = node;
            }
        }

        var positions = GraphLayout.Compute(new GraphModel(nodes, edges, 0), 900, 700);

        Assert.Equal(12, positions.Count);
        Assert.All(positions, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
    }

    [Fact]
    public void Compute_RejectsAnEmptyViewport() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GraphLayout.Compute(new GraphModel([GraphNode.ForNote(Guid.NewGuid(), "A.md")], [], 0), 0, 600));

    private static GraphModel Chain(int length)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        GraphNode? previous = null;

        for (var i = 0; i < length; i++)
        {
            var node = GraphNode.ForNote(Guid.NewGuid(), $"Note{i}.md");
            nodes.Add(node);
            if (previous is not null) edges.Add(new GraphEdge(previous.Key, node.Key, GraphEdgeKind.Link));
            previous = node;
        }

        return new GraphModel(nodes, edges, 0);
    }

    private static double Distance(GraphPosition a, GraphPosition b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
