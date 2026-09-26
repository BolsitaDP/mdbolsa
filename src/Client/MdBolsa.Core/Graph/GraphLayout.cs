namespace MdBolsa.Core.Graph;

public readonly record struct GraphPosition(string NodeKey, double X, double Y);

// Force-directed layout (Fruchterman-Reingold) in Core, not in the shell: the
// algorithm is the part worth testing, and every future client (WinUI today,
// macOS/Linux/mobile in Phase 13) needs the same positions. It is pure double
// arithmetic with a fixed iteration count and a deterministic circular seed -
// no RNG - so the same graph always lays out identically, which is what makes it
// assertable in tests and non-jarring in the UI.
public static class GraphLayout
{
    public const int DefaultIterations = 300;

    // Nodes closer than this (in pixels) are treated as coincident, so a graph
    // that legitimately contains two nodes at the same spot can't divide by zero.
    private const double MinimumDistance = 0.01;

    public static IReadOnlyList<GraphPosition> Compute(
        GraphModel graph,
        double width,
        double height,
        int iterations = DefaultIterations)
    {
        var nodes = graph.Nodes;
        var count = nodes.Count;
        if (count == 0) return [];
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(
            nameof(width), "Layout needs a positive viewport.");

        var xs = new double[count];
        var ys = new double[count];
        SeedOnCircle(nodes, xs, ys, width, height);

        if (count > 1)
        {
            // Node keys are unique by construction, so a dictionary lookup can
            // replace the linear scan the edge loop would otherwise do per edge
            // per iteration (which is the difference between milliseconds and
            // seconds at a few hundred nodes).
            var indexByKey = new Dictionary<string, int>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++) indexByKey[nodes[i].Key] = i;

            var area = width * height;
            var k = Math.Sqrt(area / count) * 0.75; // ideal edge length
            var temperature = Math.Min(width, height) / 10;
            var cooling = temperature / (iterations + 1);

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var dx = new double[count];
                var dy = new double[count];

                // Repulsion between every pair - the O(n^2) term, fine at
                // personal-vault scale and the reason the UI caps what it draws.
                for (var i = 0; i < count; i++)
                {
                    for (var j = i + 1; j < count; j++)
                    {
                        var offsetX = xs[i] - xs[j];
                        var offsetY = ys[i] - ys[j];
                        var distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
                        if (distance < MinimumDistance)
                        {
                            // Deterministic nudge based on index order, so two
                            // overlapping nodes always separate the same way.
                            offsetX = (i - j) * MinimumDistance;
                            offsetY = MinimumDistance;
                            distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
                        }

                        var force = (k * k) / distance;
                        var unitX = offsetX / distance;
                        var unitY = offsetY / distance;
                        dx[i] += unitX * force;
                        dy[i] += unitY * force;
                        dx[j] -= unitX * force;
                        dy[j] -= unitY * force;
                    }
                }

                // Attraction along edges, pulling linked notes toward the ideal
                // length.
                foreach (var edge in graph.Edges)
                {
                    var i = indexByKey.GetValueOrDefault(edge.SourceKey, -1);
                    var j = indexByKey.GetValueOrDefault(edge.TargetKey, -1);
                    if (i < 0 || j < 0 || i == j) continue;

                    var offsetX = xs[i] - xs[j];
                    var offsetY = ys[i] - ys[j];
                    var distance = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
                    if (distance < MinimumDistance) continue;

                    var force = (distance * distance) / k;
                    var unitX = offsetX / distance;
                    var unitY = offsetY / distance;
                    dx[i] -= unitX * force;
                    dy[i] -= unitY * force;
                    dx[j] += unitX * force;
                    dy[j] += unitY * force;
                }

                // Displace, clamped by the current temperature, pulling everything
                // back toward the middle so the graph can't drift off-screen.
                for (var i = 0; i < count; i++)
                {
                    var length = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                    if (length > 0)
                    {
                        var limited = Math.Min(length, temperature) / length;
                        xs[i] += dx[i] * limited;
                        ys[i] += dy[i] * limited;
                    }

                    xs[i] = (xs[i] + width / 2) / 2;
                    ys[i] = (ys[i] + height / 2) / 2;
                }

                temperature = Math.Max(temperature - cooling, 0);
            }
        }

        var margin = NodeRadius;
        var positions = new GraphPosition[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = new GraphPosition(
                nodes[i].Key,
                Math.Clamp(xs[i], margin, width - margin),
                Math.Clamp(ys[i], margin, height - margin));
        }

        return positions;
    }

    // Node radius in layout units, so the clamp above keeps a node's circle (and
    // its label) inside the viewport. The UI draws nodes at this size too.
    public const double NodeRadius = 18;

    private static void SeedOnCircle(IReadOnlyList<GraphNode> nodes, double[] xs, double[] ys, double width, double height)
    {
        var centerX = width / 2;
        var centerY = height / 2;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes.Count == 1)
            {
                xs[i] = centerX;
                ys[i] = centerY;
                continue;
            }

            var angle = 2 * Math.PI * i / nodes.Count;
            var             radius = Math.Min(width, height) / 3;
            xs[i] = centerX + radius * Math.Cos(angle);
            ys[i] = centerY + radius * Math.Sin(angle);
        }
    }
}
