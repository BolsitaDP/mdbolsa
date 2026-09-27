namespace MdBolsa.Core.Diagrams;

/// <summary>
/// Lays a flowchart out in ranks, the way Mermaid's own diagrams come out: a
/// chain or a tree runs in a clean line with its branches beside each other.
///
/// The knowledge graph uses a force-directed layout, which is right for a
/// relationship map and wrong for a flowchart - nobody draws a decision tree and
/// expects it to come out a hairball. So this is a different algorithm, but the
/// same discipline: pure arithmetic in Core, no randomness, fixed iteration
/// counts, so the same diagram always produces the same picture and the tests can
/// assert on it.
///
/// The shape of the result is two axes. The **rank axis** is the direction of
/// flow: rank 0 is at the start, rank 1 next to it, and so on. The **cross axis**
/// is where the branches sit beside each other. Every decision below is about
/// those two numbers, and the direction is applied once at the end by swapping or
/// flipping them.
///
/// Three details that matter more than they look:
///
/// * **Cycles.** `A --&gt; B --&gt; A` has no rank order, and a ranker that loops
///   forever is worse than one that gives up. Ranks relax a bounded number of
///   times, so a cycle leaves its members sharing a rank, which reads fine.
/// * **Crossings.** Barycentre ordering runs twice: each node is placed next to
///   the average position of its parents, which is what stops
///   `A --&gt; B`, `A --&gt; C`, `D --&gt; C` from drawing C on the far side of B.
///   Two passes is plenty for note-sized diagrams and the cost is O(edges).
/// * **Labelled edges need room.** `A --&gt;|yes| B` is taller than `A --&gt; B`,
///   and a layout that gives both the same gap draws the label on top of the
///   arrow. The gap is per boundary, not global, so one labelled branch does not
///   push the whole diagram apart.
/// </summary>
public static class FlowchartLayout
{
    private const double RankGap = 44;
    private const double LabelledRankGap = 62;
    private const double NodeGap = 24;

    // Roughly a character at the diagram's font size. Only used to size the box
    // before anything is drawn; the shell fits the real text to it afterwards.
    private const double CharacterWidth = 7.2;
    private const double MinimumNodeWidth = 76;
    private const double MaximumNodeWidth = 220;
    private const double NodeHeight = 34;

    /// <summary>How many times ranks are relaxed before the ranker stops. Bounded on purpose - see cycles above.</summary>
    private const int MaxRankPasses = 12;

    /// <summary>
    /// How far a diagram may be shrunk to fit its pane.
    ///
    /// This is the one number in the layout that is a judgement rather than a
    /// derivation, so it is worth being explicit about what it is protecting.
    /// Shrinking a diagram scales its boxes, and a box holds fewer characters as it
    /// gets smaller. Below about 0.8 the truncation eats words: at 0.6 a node
    /// labelled "Cilindro" showed "Cílin...", which is a shape that does not exist.
    ///
    /// So past this point the layout stops shrinking and reports a content extent
    /// larger than the space it was given, and the shell turns that into a
    /// scrollbar. A diagram you have to scroll is a small price; a diagram full of
    /// invented words is not a price at all.
    ///
    /// 0.8 rather than 1.0 because vertical shrinking is still worth having: a
    /// tall diagram that fits without the scrollbar is calmer to read, and at 0.8
    /// the words are still whole.
    /// </summary>
    public const double MinimumScale = 0.8;

    /// <summary>
    /// The space the laid-out diagram actually occupies, which is at least the
    /// space it was given and sometimes more (see <see cref="MinimumScale"/>).
    /// The shell needs this to size a scroll area.
    /// </summary>
    public static (double Width, double Height) Bounds(IReadOnlyList<DiagramPosition> positions)
    {
        if (positions.Count == 0) return (0, 0);

        return (
            positions.Max(p => p.X + p.Width / 2) - positions.Min(p => p.X - p.Width / 2),
            positions.Max(p => p.Y + p.Height / 2) - positions.Min(p => p.Y - p.Height / 2));
    }

    /// <summary>
    /// Lays the flowchart out against a width only, with the result's top-left at
    /// the origin, and reports the natural height.
    ///
    /// This exists because of a complaint that was entirely fair: hovering a
    /// diagram and scrolling scrolled *the diagram*, and scrolling the page required
    /// moving the mouse off it first. A second scrollbar inside the first one is
    /// always a mistake - the reader has to learn which one the wheel belongs to.
    ///
    /// So a diagram has no vertical scroll of its own. It is as tall as it needs to
    /// be, and the page's own scrollbar is the only vertical one. Height is
    /// therefore not a constraint here: it is an output.
    ///
    /// The height passed to <see cref="Compute"/> is deliberately enormous so the
    /// vertical axis never binds, and the result is then shifted up to the origin
    /// because a diagram centred inside a very tall virtual space is still a
    /// diagram of the same size, just far from the top.
    /// </summary>
    public static IReadOnlyList<DiagramPosition> ComputeFlowing(
        Flowchart flowchart, double width, out double naturalHeight)
    {
        if (flowchart.Nodes.Count == 0)
        {
            naturalHeight = 0;
            return [];
        }

        // Large enough that no realistic diagram is height-constrained by it, and
        // finite so the arithmetic below is finite too.
        const int Unconstrained = 1_000_000;

        var positions = Compute(flowchart, width, Unconstrained, out _);
        if (positions.Count == 0)
        {
            naturalHeight = 0;
            return positions;
        }

        var top = positions.Min(p => p.Y - p.Height / 2);
        var left = positions.Min(p => p.X - p.Width / 2);

        naturalHeight = positions.Max(p => p.Y + p.Height / 2) - top;

        return positions.Select(position => position with
        {
            X = position.X - left,
            Y = position.Y - top,
        }).ToList();
    }

    public static IReadOnlyList<DiagramPosition> Compute(
        Flowchart flowchart, double width, double height) =>
        Compute(flowchart, width, height, out _);

    /// <summary>
    /// Lays the flowchart out and reports the scale that was applied.
    ///
    /// The caller needs it: a box that is 60% of its natural size has to hold 60%
    /// of its text, or the labels get truncated to their first few letters and the
    /// diagram says "Escribes una nota" as "Escribe...". Scaling the font is the
    /// shell's job - Core has no font - but it can only do it if it is told.
    /// </summary>
    public static IReadOnlyList<DiagramPosition> Compute(
        Flowchart flowchart, double width, double height, out double scale)
    {
        if (flowchart.Nodes.Count == 0)
        {
            scale = 1;
            return [];
        }

        var horizontal = flowchart.Direction is DiagramDirection.LeftRight or DiagramDirection.RightLeft;
        var ranks = AssignRanks(flowchart);
        var order = OrderWithinRanks(flowchart, ranks);
        var membersByRank = GroupByRank(order, ranks);

        // The rank axis: every rank gets a slot sized to its largest member, and
        // the slots are laid end to end.
        var rankExtent = membersByRank.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Max(index => SizeAlong(flowchart.Nodes[index], horizontal)));

        var labelledBoundaries = LabelledRankBoundaries(flowchart, ranks);

        var rankStart = new Dictionary<int, double>();
        var totalAlong = 0.0;
        var first = true;

        foreach (var rank in membersByRank.Keys)
        {
            if (!first) totalAlong += labelledBoundaries.Contains(rank) ? LabelledRankGap : RankGap;
            first = false;

            rankStart[rank] = totalAlong;
            totalAlong += rankExtent[rank];
        }

        // The cross axis: where each member wants to sit, based on where its
        // parents ended up. This is the part a "just centre every row" layout
        // cannot do - a rank holding a single node still belongs next to the branch
        // it came from, not in the middle of the diagram.
        var across = new Dictionary<int, double>();
        var parents = ParentsOf(flowchart);

        foreach (var rank in membersByRank.Keys)
        {
            var members = membersByRank[rank];

            for (var i = 0; i < members.Count; i++)
            {
                var node = members[i];
                var size = SizeAcross(flowchart.Nodes[node], horizontal);

                // Only parents already placed. In a cycle a node's "parent" can sit
                // in a later rank, and waiting for it would either deadlock or need
                // a guess; ignoring the unplaced ones and falling back to the
                // node's own size is the honest version.
                var placed = parents[node].Where(parent => across.ContainsKey(parent)).ToList();

                across[node] = placed.Count == 0
                    ? size / 2
                    : placed.Average(parent => across[parent]);
            }

            // Push apart only as far as needed to stop the boxes touching, then
            // pull back towards what was wanted. One pass each way is enough at
            // note sizes, and it is stable - running it twice gives the same answer.
            var cursor = double.NegativeInfinity;
            for (var i = 0; i < members.Count; i++)
            {
                var node = members[i];
                var size = SizeAcross(flowchart.Nodes[node], horizontal);
                var minimum = cursor + size / 2 + (i == 0 ? 0 : NodeGap);

                across[node] = Math.Max(across[node], minimum);
                cursor = across[node] + size / 2;
            }

            for (var i = members.Count - 1; i >= 0; i--)
            {
                var node = members[i];
                var size = SizeAcross(flowchart.Nodes[node], horizontal);

                if (i == members.Count - 1) continue;

                var next = members[i + 1];
                var ceiling = across[next] - size / 2 - NodeGap;
                if (across[node] > ceiling) across[node] = ceiling;
            }
        }

        // Centre the whole board on the cross axis once, rather than per rank, so
        // the diagram sits in the middle of whatever space it is given. The extent
        // is tracked as the boxes are placed, because a node's own half-width is
        // what reaches past its centre.
        var leftmost = double.MaxValue;
        var rightmost = double.MinValue;

        foreach (var (rank, members) in membersByRank)
        {
            foreach (var index in members)
            {
                var half = SizeAcross(flowchart.Nodes[index], horizontal) / 2;
                leftmost = Math.Min(leftmost, across[index] - half);
                rightmost = Math.Max(rightmost, across[index] + half);
            }
        }

        var crossShift = -(leftmost + rightmost) / 2;

        var flipped = flowchart.Direction is DiagramDirection.BottomUp or DiagramDirection.RightLeft;
        var positions = new List<DiagramPosition>(flowchart.Nodes.Count);

        foreach (var (rank, members) in membersByRank)
        {
            var alongCentre = rankStart[rank] + rankExtent[rank] / 2;
            var along = flipped ? totalAlong - alongCentre : alongCentre;

            foreach (var index in members)
            {
                var node = flowchart.Nodes[index];
                var acrossCentre = across[index] + crossShift;

                // Width and height are always the x and y extents, whatever the
                // direction - swapping them for a top-down diagram puts a node's
                // *height* in the field every consumer reads as its width, and the
                // bounding box then does not contain the box.
                positions.Add(new DiagramPosition(
                    node.Id,
                    horizontal ? along : acrossCentre,
                    horizontal ? acrossCentre : along,
                    horizontal ? SizeAlong(node, true) : SizeAcross(node, false),
                    horizontal ? SizeAcross(node, true) : SizeAlong(node, false),
                    rank));
            }
        }

        return Fit(positions, width, height, out scale);
    }

    private static double SizeAlong(DiagramNode node, bool horizontal) =>
        horizontal ? NodeWidthFor(node) : NodeHeight;

    private static double SizeAcross(DiagramNode node, bool horizontal) =>
        horizontal ? NodeHeight : NodeWidthFor(node);

    private static double NodeWidthFor(DiagramNode node) =>
        Math.Clamp(node.Label.Length * CharacterWidth + 26, MinimumNodeWidth, MaximumNodeWidth);

    private static IEnumerable<int> Enumerate(IEnumerable<int> ranks) => ranks.OrderBy(rank => rank);

    // --- Ranks ------------------------------------------------------------

    private static int[] AssignRanks(Flowchart flowchart)
    {
        var count = flowchart.Nodes.Count;
        var indexById = IndexById(flowchart);

        var ranks = new int[count];

        // Relax longest-path: a node sits one rank below its deepest parent. The
        // relaxation is bounded, so a cycle terminates instead of oscillating -
        // its members just end up sharing a rank.
        for (var pass = 0; pass < MaxRankPasses; pass++)
        {
            var changed = false;

            foreach (var edge in flowchart.Edges)
            {
                if (!indexById.TryGetValue(edge.FromId, out var from) ||
                    !indexById.TryGetValue(edge.ToId, out var to))
                {
                    continue;
                }

                if (ranks[to] >= ranks[from] + 1) continue;

                ranks[to] = ranks[from] + 1;
                changed = true;
            }

            if (!changed) break;
        }

        return ranks;
    }

    /// <summary>Rank numbers that have a labelled edge arriving into them.</summary>
    private static HashSet<int> LabelledRankBoundaries(Flowchart flowchart, int[] ranks)
    {
        var indexById = IndexById(flowchart);
        var boundaries = new HashSet<int>();

        foreach (var edge in flowchart.Edges)
        {
            if (string.IsNullOrEmpty(edge.Label)) continue;
            if (!indexById.TryGetValue(edge.ToId, out var to)) continue;

            boundaries.Add(ranks[to]);
        }

        return boundaries;
    }

    /// <summary>
    /// Each node's incoming edges, as node indices. A node with no parents is a
    /// root, and roots are placed from their own size rather than from an average
    /// of nothing.
    /// </summary>
    private static List<int>[] ParentsOf(Flowchart flowchart)
    {
        var count = flowchart.Nodes.Count;
        var indexById = IndexById(flowchart);

        var parents = new List<int>[count];
        for (var i = 0; i < count; i++) parents[i] = [];

        foreach (var edge in flowchart.Edges)
        {
            if (!indexById.TryGetValue(edge.FromId, out var from) ||
                !indexById.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            // A self-loop is not a placement hint. Keeping it would make the node
            // its own parent, and it would then be waiting for a position it is
            // itself supposed to produce.
            if (from == to) continue;

            parents[to].Add(from);
        }

        return parents;
    }

    private static Dictionary<string, int> IndexById(Flowchart flowchart)
    {
        var indexById = new Dictionary<string, int>(flowchart.Nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < flowchart.Nodes.Count; i++) indexById[flowchart.Nodes[i].Id] = i;
        return indexById;
    }

    private static Dictionary<int, List<int>> GroupByRank(List<int> order, int[] ranks) =>
        order.GroupBy(index => ranks[index])
            .ToDictionary(group => group.Key, group => group.ToList(), EqualityComparer<int>.Default)
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value, EqualityComparer<int>.Default);

    // --- Ordering within a rank -------------------------------------------

    private static List<int> OrderWithinRanks(Flowchart flowchart, int[] ranks)
    {
        var count = flowchart.Nodes.Count;
        var indexById = IndexById(flowchart);

        var parents = new List<int>[count];
        for (var i = 0; i < count; i++) parents[i] = [];

        foreach (var edge in flowchart.Edges)
        {
            if (indexById.TryGetValue(edge.FromId, out var from) &&
                indexById.TryGetValue(edge.ToId, out var to))
            {
                parents[to].Add(from);
            }
        }

        // Stable throughout: ties break on the previous order, so two nodes in the
        // same place never swap between runs. Non-determinism here would make the
        // diagram jump about on every re-render, which is worse than a crossing.
        var order = Enumerable.Range(0, count).ToList();

        for (var pass = 0; pass < 2; pass++)
        {
            var position = order.Select((node, at) => (node, at)).ToDictionary(pair => pair.node, pair => pair.at);

            order = order
                .OrderBy(node => ranks[node])
                .ThenBy(node => parents[node].Count == 0
                    ? position[node]
                    : parents[node].Average(parent => position[parent]))
                .ThenBy(node => position[node])
                .ToList();
        }

        return order;
    }

    // --- Fitting ----------------------------------------------------------

    /// <summary>
    /// Centres the diagram in the space it will be drawn in, scaling it down only
    /// if it does not fit.
    ///
    /// A diagram bigger than its pane shrinks; a diagram smaller than its pane is
    /// centred at 1:1 rather than blown up, because enlarging makes the labels
    /// diverge from the text around them and a small diagram blown to fill a
    /// sidebar looks like a mistake.
    ///
    /// The sizes are scaled along with the centres. Scaling only the centres pulls
    /// them together while the boxes stay full size, and the result is a diagram
    /// whose boxes hang off both edges of the pane - the overflow the scaling was
    /// supposed to prevent. The shell draws each box to the size given here, so the
    /// two always agree.
    /// </summary>
    private static IReadOnlyList<DiagramPosition> Fit(
        IReadOnlyList<DiagramPosition> positions, double width, double height, out double scale)
    {
        scale = 1;
        if (positions.Count == 0 || width <= 0 || height <= 0) return positions;

        var minX = positions.Min(p => p.X - p.Width / 2);
        var maxX = positions.Max(p => p.X + p.Width / 2);
        var minY = positions.Min(p => p.Y - p.Height / 2);
        var maxY = positions.Max(p => p.Y + p.Height / 2);

        var contentWidth = Math.Max(maxX - minX, 1);
        var contentHeight = Math.Max(maxY - minY, 1);

        const double Padding = 20;
        scale = Math.Max(
            MinimumScale,
            Math.Min(
                1.0,
                Math.Min(
                    Math.Max(width - Padding * 2, 1) / contentWidth,
                    Math.Max(height - Padding * 2, 1) / contentHeight)));

        var centreX = (minX + maxX) / 2;
        var centreY = (minY + maxY) / 2;

        // Copied out because `scale` is an out parameter and cannot be captured by
        // the projection below.
        var factor = scale;

        return positions.Select(position => position with
        {
            X = (position.X - centreX) * factor + width / 2,
            Y = (position.Y - centreY) * factor + height / 2,
            Width = position.Width * factor,
            Height = position.Height * factor,
        }).ToList();
    }
}
