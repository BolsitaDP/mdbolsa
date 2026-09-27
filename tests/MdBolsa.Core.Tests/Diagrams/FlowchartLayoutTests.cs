using MdBolsa.Core.Diagrams;

namespace MdBolsa.Core.Tests.Diagrams;

// The ranked layout. What matters is not that positions are "reasonable" but that
// they are the *right kind* of reasonable: a chain runs in a line, branches sit
// beside each other, and the same input always produces the same picture. Those
// three are assertable. "Looks about right" is not, and it is exactly the kind of
// property that silently regresses.
public class FlowchartLayoutTests
{
    [Fact]
    public void A_chain_runs_in_a_line_down_the_page()
    {
        var positions = Layout("flowchart TD\n  A[a] --> B[b] --> C[c]");

        Assert.Equal(0, positions["A"].Rank);
        Assert.Equal(1, positions["B"].Rank);
        Assert.Equal(2, positions["C"].Rank);

        // Top to bottom, each step further down than the last. This is the whole
        // difference between a flowchart and a hairball.
        Assert.True(positions["B"].Y > positions["A"].Y);
        Assert.True(positions["C"].Y > positions["B"].Y);
    }

    [Fact]
    public void A_chain_runs_across_the_page_when_the_direction_is_left_to_right()
    {
        var positions = Layout("flowchart LR\n  A[a] --> B[b] --> C[c]");

        Assert.True(positions["B"].X > positions["A"].X);
        Assert.True(positions["C"].X > positions["B"].X);
    }

    [Fact]
    public void Bottom_up_flips_the_picture_without_changing_the_ranks()
    {
        var down = Layout("flowchart TD\n  A[a] --> B[b] --> C[c]");
        var up = Layout("flowchart BT\n  A[a] --> B[b] --> C[c]");

        // Same ranks - the direction is a presentation choice, not a different
        // graph - but the start ends up at the bottom.
        Assert.Equal(down["A"].Rank, up["A"].Rank);
        Assert.True(up["A"].Y > up["B"].Y);
        Assert.True(up["B"].Y > up["C"].Y);
    }

    [Fact]
    public void Branches_share_a_rank_and_are_separated()
    {
        var positions = Layout("flowchart TD\n  A[a] --> B[b]\n  A --> C[c]\n  A --> D[d]");

        Assert.Equal(positions["B"].Rank, positions["C"].Rank);
        Assert.Equal(positions["C"].Rank, positions["D"].Rank);

        // Beside each other, not stacked on top of each other. Overlapping nodes
        // are the failure a reader notices immediately and cannot interpret.
        var xs = new[] { positions["B"].X, positions["C"].X, positions["D"].X }.OrderBy(x => x).ToList();
        Assert.True(xs[1] - xs[0] > 0, "B and C overlap");
        Assert.True(xs[2] - xs[1] > 0, "C and D overlap");
    }

    [Fact]
    public void Two_nodes_are_never_at_the_same_point()
    {
        // Every layout has to survive this, including a cycle where the ranker
        // gives up and puts members on the same rank.
        var positions = Layout("flowchart LR\n  A --> B --> A");

        var points = positions.Values.Select(p => (p.X, p.Y)).ToList();
        Assert.Equal(points.Count, points.Distinct().Count());
    }

    [Fact]
    public void A_cycle_terminates_instead_of_running_away()
    {
        // The relaxation is bounded on purpose. An unbounded longest-path ranker
        // does not hang here - it just never settles, and the symptom is a layout
        // that takes longer the more edges a note has.
        var positions = Layout("flowchart LR\n  A --> B --> C --> A");

        Assert.Equal(3, positions.Count);
        Assert.All(positions.Values, position => Assert.True(double.IsFinite(position.X)));
    }

    [Fact]
    public void A_self_loop_does_not_break_the_layout()
    {
        Assert.Single(Layout("flowchart TD\n  A[a] --> A"));
    }

    [Fact]
    public void A_converging_node_is_placed_near_the_parent_that_makes_it_unique()
    {
        // A --> B, A --> C, D --> C. B and C share a rank, correctly: both hang
        // off something in rank 0. What should not happen is C sitting under A,
        // because A already has B and C is really D's node too - the edge from D
        // would then run the full width of the diagram to reach it.
        var positions = Layout("flowchart TD\n  A[a] --> B[b]\n  A --> C[c]\n  D[d] --> C");

        Assert.Equal(positions["B"].Rank, positions["C"].Rank);

        var a = positions["A"].X;
        var c = positions["C"].X;
        var d = positions["D"].X;

        Assert.True(
            Math.Abs(c - d) < Math.Abs(c - a),
            $"C at {c} should sit nearer D at {d} than A at {a}");
    }

    [Fact]
    public void A_labelled_edge_gets_more_room_than_a_plain_one()
    {
        // `A -->|yes| B` is taller than `A --> B`, and the same gap for both draws
        // the label on top of the arrow.
        var plain = Layout("flowchart TD\n  A[a] --> B[b]");
        var labelled = Layout("flowchart TD\n  A[a] -->|a long label| B[b]");

        var plainGap = plain["B"].Y - plain["A"].Y;
        var labelledGap = labelled["B"].Y - labelled["A"].Y;

        Assert.True(labelledGap > plainGap, $"{labelledGap} should exceed {plainGap}");
    }

    [Fact]
    public void Everything_is_inside_the_space_it_was_given()
    {
        // The whole reason Fit exists: a diagram that runs off the side of the
        // preview pane is a diagram nobody can read.
        var positions = Layout(
            "flowchart TD\n  A[a] --> B[b] --> C[c]\n  A --> D[d] --> C", 400, 300);

        Assert.All(positions.Values, position =>
        {
            Assert.InRange(position.X - position.Width / 2, 0, 400);
            Assert.InRange(position.Y - position.Height / 2, 0, 300);
        });
    }

    [Fact]
    public void A_diagram_too_big_for_its_pane_stops_shrinking_rather_than_becoming_unreadable()
    {
        // Twelve nodes across a 300px pane. The layout will not shrink this to
        // fit: below MinimumScale the labels are stubs, and a diagram of stubs
        // that fits perfectly is worse than one you have to scroll.
        var wide = Layout(
            "flowchart LR\n  A --> B --> C --> D --> E --> F --> G --> H --> I --> J --> K --> L",
            300, 200);

        var extent = FlowchartLayout.Bounds(wide.Values.ToList());

        Assert.True(extent.Width > 300,
            $"expected an overflowing extent, got {extent.Width:F0}");

        // And the boxes are still a readable size, which is the whole point. The
        // extent itself is not bounded by the frame: at the floor it is simply the
        // natural size times 0.6, which for twelve nodes is wider than the pane, and
        // that is the scrollbar's business rather than this test's.
        Assert.All(wide.Values, position => Assert.True(
            position.Width >= 45,
            $"{position.NodeId} shrank to {position.Width:F0}px, which is not readable"));
    }

    [Fact]
    public void A_small_diagram_is_centred_rather_than_stretched()
    {
        // Enlarging a two-node diagram to fill a tall pane makes the labels look
        // like they belong to a different app.
        var positions = Layout("flowchart TD\n  A[a] --> B[b]", 900, 800);

        var centreY = positions.Values.Average(p => p.Y);
        Assert.InRange(centreY, 380, 420);
    }

    [Fact]
    public void A_long_label_makes_its_node_wider_but_not_unbounded()
    {
        var brief = Layout("flowchart TD\n  A[ok] --> B[b]");
        var longer = Layout("flowchart TD\n  A[\"a considerably longer label than the other one\"] --> B[b]");

        Assert.True(longer["A"].Width > brief["A"].Width);

        // Capped, so one enormous label cannot push the rest of the diagram off
        // the page.
        Assert.True(longer["A"].Width <= 230, $"width was {longer["A"].Width}");
    }

    [Fact]
    public void The_same_diagram_always_lays_out_identically()
    {
        // No RNG anywhere, and every ordering tie breaks on the previous order.
        // Non-determinism here shows up as a diagram that jumps about each time the
        // preview re-renders, which is maddening and looks like a bug in the app.
        const string source = """
            flowchart TD
              A[start] --> B{ok?}
              B -->|yes| C[do it]
              B -->|no| D[stop]
              C --> E[done]
              D --> E
            """;

        var first = Layout(source);
        var second = Layout(source);

        Assert.Equal(
            first.OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key, pair.Value.X, pair.Value.Y, pair.Value.Rank)),
            second.OrderBy(pair => pair.Key)
                .Select(pair => (pair.Key, pair.Value.X, pair.Value.Y, pair.Value.Rank)));
    }

    [Fact]
    public void An_empty_diagram_lays_out_to_nothing_rather_than_throwing()
    {
        Assert.Empty(FlowchartLayout.Compute(Flowchart.Empty, 400, 300));
    }

    [Fact]
    public void The_layout_reports_the_scale_it_applied_so_the_font_can_follow_it()
    {
        // A box shrinks with the diagram, so its text has to shrink too, or
        // "Escribes una nota" becomes "Escribe...". Core has no font and cannot
        // apply this itself - it can only report the scale, and the shell needs it
        // to be told. The first version did not report it, and every label in a
        // shrunken diagram was truncated.
        Assert.True(FlowchartParser.TryParse("flowchart TD\n  A[a] --> B[b]", out var chart, out _));

        var atFullSize = FlowchartLayout.Compute(chart, 2000, 2000, out var big);
        Assert.Equal(1.0, big);

        var atMinimum = FlowchartLayout.Compute(chart, 40, 40, out var small);
        Assert.Equal(FlowchartLayout.MinimumScale, small);

        // And the boxes really are smaller at the small size - otherwise the scale
        // is being reported but not applied, which is the other half of the bug.
        Assert.True(atMinimum.Max(p => p.Width) < atFullSize.Max(p => p.Width));
    }

    [Fact]
    public void A_diagram_with_nodes_but_no_edges_still_lays_out()
    {
        var positions = Layout("flowchart TD\n  A[a]\n  B[b]\n  C[c]");

        Assert.Equal(3, positions.Count);
        Assert.All(positions.Values, position => Assert.True(double.IsFinite(position.X)));
    }

    [Fact]
    public void A_zero_sized_viewport_does_not_produce_NaN()
    {
        // This happens for real: the preview is built before the pane has been
        // measured, and a NaN coordinate there draws nothing at all.
        Assert.True(FlowchartParser.TryParse("flowchart TD\n  A[a] --> B[b]", out var chart, out _));

        var positions = FlowchartLayout.Compute(chart, 0, 0);

        Assert.All(positions, position => Assert.True(double.IsFinite(position.X)));
    }

    // --- Plumbing -----------------------------------------------------------

    private static Dictionary<string, DiagramPosition> Layout(
        string source, double width = 800, double height = 600)
    {
        Assert.True(FlowchartParser.TryParse(source, out var chart, out var error), error?.ToString());

        return FlowchartLayout.Compute(chart, width, height)
            .ToDictionary(position => position.NodeId);
    }
}
