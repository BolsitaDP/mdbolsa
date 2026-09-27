using MdBolsa.Core.Diagrams;

namespace MdBolsa.Core.Tests.Diagrams;

// The Mermaid flowchart subset, parsed by hand.
//
// The tests are the argument for hand-writing it. Every one here is a shape of
// input that a real note contains and that a naive reader gets wrong, and the
// failure mode they all guard is the same: a diagram that is *plausible* and
// *wrong*. A code block you can see is a nuisance; a flowchart that quietly drops
// an arrow or truncates a label is a lie, and the reader has no way to tell.
//
// Every positive assertion carries the parser's own objection as its message. A
// test that fails without saying what the parser disliked is a test you have to
// debug a second time to find out.
public class FlowchartParserTests
{
    [Fact]
    public void A_minimal_flowchart_parses()
    {
        Assert.True(Parse("flowchart TD\n  A[Start] --> B[End]", out var chart, out var error), error?.ToString());

        Assert.Equal(DiagramDirection.TopDown, chart.Direction);
        Assert.Equal(2, chart.NodeCount);
        Assert.Equal(1, chart.EdgeCount);
        Assert.Equal("Start", chart.Find("A")!.Label);
        Assert.Equal("End", chart.Find("B")!.Label);
        Assert.Equal(DiagramShape.Rectangle, chart.Find("A")!.Shape);
    }

    [Theory]
    [InlineData("graph TD", DiagramDirection.TopDown)]
    [InlineData("graph LR", DiagramDirection.LeftRight)]
    [InlineData("flowchart TD", DiagramDirection.TopDown)]
    [InlineData("flowchart TB", DiagramDirection.TopDown)]
    [InlineData("flowchart BT", DiagramDirection.BottomUp)]
    [InlineData("flowchart LR", DiagramDirection.LeftRight)]
    [InlineData("flowchart RL", DiagramDirection.RightLeft)]
    public void Directions_and_the_graph_spelling_are_both_understood(string header, DiagramDirection expected)
    {
        Assert.True(Parse($"{header}\n A --> B", out var chart, out var error), error?.ToString());
        Assert.Equal(expected, chart.Direction);
    }

    [Theory]
    [InlineData("A[Rectangle]", DiagramShape.Rectangle)]
    [InlineData("A(Rounded)", DiagramShape.Rounded)]
    [InlineData("A([Stadium])", DiagramShape.Stadium)]
    [InlineData("A[[Subroutine]]", DiagramShape.Subroutine)]
    [InlineData("A[(Cylinder)]", DiagramShape.Cylinder)]
    [InlineData("A((Double))", DiagramShape.DoubleCircle)]
    [InlineData("A{Diamond}", DiagramShape.Diamond)]
    [InlineData("A{{Hexagon}}", DiagramShape.Hexagon)]
    [InlineData("A>Asymmetric]", DiagramShape.Asymmetric)]
    [InlineData("A[/Parallel/]", DiagramShape.Parallelogram)]
    [InlineData("A[\\Alternate\\]", DiagramShape.ParallelogramAlt)]
    [InlineData("A[/Trap\\]", DiagramShape.Trapezoid)]
    [InlineData("A[\\TrapAlt/]", DiagramShape.TrapezoidAlt)]
    public void Every_shape_reads_its_own_delimiters(string spec, DiagramShape expected)
    {
        // Each of these has a sibling that starts with the same character - `[[`
        // against `[`, `((` against `(`, `{{` against `{` - so confusing them is
        // the easy mistake, and each is pinned separately.
        Assert.True(Parse($"flowchart TD\n  {spec}", out var chart, out var error), error?.ToString());
        Assert.Equal(expected, chart.Find("A")!.Shape);
    }

    [Fact]
    public void A_label_may_contain_its_own_brackets()
    {
        // The reason the close is found by counting depth instead of at the first
        // `]`. Read naively this is "what if [this" followed by junk.
        Assert.True(Parse("flowchart TD\n  A[what if [this] happens?]", out var chart, out var error), error?.ToString());

        Assert.Equal("what if [this] happens?", chart.Find("A")!.Label);
    }

    [Fact]
    public void A_quoted_label_may_contain_anything()
    {
        Assert.True(Parse(
            "flowchart TD\n  A[\"a (b) [c] {d} -e-> f\"] --> B[x]", out var chart, out var error), error?.ToString());

        Assert.Equal("a (b) [c] {d} -e-> f", chart.Find("A")!.Label);
        Assert.Equal(1, chart.EdgeCount);
    }

    [Fact]
    public void A_quoted_label_closes_its_shape_and_the_next_statement_still_reads()
    {
        // The quote ends the *label*; the `]` ends the *shape*. Consuming only the
        // first leaves a stray `]` and the next statement never parses.
        Assert.True(Parse(
            "flowchart LR\n  A[\"quoted\"] --> B[plain]", out var chart, out var error), error?.ToString());

        Assert.Equal(2, chart.NodeCount);
        Assert.Equal(1, chart.EdgeCount);
        Assert.Equal("plain", chart.Find("B")!.Label);
    }

    [Fact]
    public void Quoted_labels_unescape_doubled_quotes()
    {
        Assert.True(Parse(
            "flowchart TD\n  A[\"say \"\"hello\"\"\"]", out var chart, out var error), error?.ToString());

        Assert.Equal("say \"hello\"", chart.Find("A")!.Label);
    }

    [Theory]
    [InlineData("A --> B", DiagramEdgeStyle.Solid)]
    [InlineData("A --- B", DiagramEdgeStyle.Solid)]
    [InlineData("A -.-> B", DiagramEdgeStyle.Dotted)]
    [InlineData("A -.- B", DiagramEdgeStyle.Dotted)]
    [InlineData("A ==> B", DiagramEdgeStyle.Thick)]
    [InlineData("A === B", DiagramEdgeStyle.Thick)]
    public void All_four_edge_styles_read(string statement, DiagramEdgeStyle expected)
    {
        Assert.True(Parse($"flowchart TD\n  {statement}", out var chart, out var error), error?.ToString());
        Assert.Equal(expected, chart.Edges[0].Style);
    }

    [Fact]
    public void Edge_labels_read_in_both_spellings()
    {
        // Mermaid allows `A -->|yes| B` and `A -- yes --> B`, and notes in the wild
        // use both. Reading only one would drop half the labels people write.
        Assert.True(Parse("flowchart TD\n  A -->|yes| B\n  C -- no --> D", out var chart, out var error), error?.ToString());

        Assert.Equal(2, chart.EdgeCount);
        Assert.Equal("yes", chart.Edges[0].Label);
        Assert.Equal("no", chart.Edges[1].Label);
    }

    [Fact]
    public void A_chain_becomes_one_edge_per_arrow()
    {
        Assert.True(Parse("flowchart TD\n  A --> B --> C --> D", out var chart, out var error), error?.ToString());

        Assert.Equal(4, chart.NodeCount);
        Assert.Equal(3, chart.EdgeCount);
        Assert.Equal(("A", "B"), (chart.Edges[0].FromId, chart.Edges[0].ToId));
        Assert.Equal(("C", "D"), (chart.Edges[2].FromId, chart.Edges[2].ToId));
    }

    [Fact]
    public void A_bare_node_is_its_own_label()
    {
        Assert.True(Parse("flowchart TD\n  A --> B[Only B has a shape]", out var chart, out var error), error?.ToString());

        Assert.Equal("A", chart.Find("A")!.Label);
        Assert.Equal("Only B has a shape", chart.Find("B")!.Label);
    }

    [Fact]
    public void A_node_mentioned_twice_appears_once()
    {
        // `A --> B` then `B --> C` is the normal way to write a chain. Two nodes
        // called B would draw two boxes and split the edges between them.
        Assert.True(Parse("flowchart TD\n  A --> B\n  B --> C", out var chart, out var error), error?.ToString());

        Assert.Equal(3, chart.NodeCount);
        Assert.Equal(2, chart.EdgeCount);
    }

    [Fact]
    public void A_later_shape_upgrades_a_bare_node()
    {
        Assert.True(Parse("flowchart TD\n  A --> B\n  B{Diamond}", out var chart, out var error), error?.ToString());

        Assert.Equal(2, chart.NodeCount);
        Assert.Equal(DiagramShape.Diamond, chart.Find("B")!.Shape);
        Assert.Equal("Diamond", chart.Find("B")!.Label);
    }

    [Fact]
    public void Subgraphs_collect_their_members()
    {
        Assert.True(Parse("""
            flowchart TD
              subgraph Setup
                A[Install] --> B[Configure]
              end
              subgraph Run
                C[Start]
              end
              B --> C
            """, out var chart, out var error), error?.ToString());

        Assert.Equal(new[] { "Setup" }, chart.Find("A")!.Groups);
        Assert.Equal(new[] { "Setup" }, chart.Find("B")!.Groups);
        Assert.Equal(new[] { "Run" }, chart.Find("C")!.Groups);
    }

    [Fact]
    public void Nested_subgroups_list_the_outermost_first()
    {
        Assert.True(Parse("""
            flowchart TD
              subgraph Outer
                subgraph Inner
                  A[x]
                end
              end
            """, out var chart, out var error), error?.ToString());

        Assert.Equal(new[] { "Outer", "Inner" }, chart.Find("A")!.Groups);
    }

    [Fact]
    public void A_subgraph_title_is_kept_as_its_own_caption_node()
    {
        Assert.True(Parse("""
            flowchart TD
              subgraph one [First steps]
                A[x]
              end
            """, out var chart, out var error), error?.ToString());

        Assert.NotNull(chart.Find("one"));
        Assert.NotNull(chart.Find("sub:one"));
        Assert.Equal("First steps", chart.Find("sub:one")!.Label);
    }

    [Fact]
    public void An_unclosed_subgraph_is_an_error_rather_than_a_guess()
    {
        // Silently closing it at the end of the note would produce a diagram that
        // looks right and is not what was written.
        Assert.False(Parse("flowchart TD\n  subgraph S\n    A[x]", out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("never closed", error!.Reason);
    }

    [Fact]
    public void An_end_with_no_subgraph_is_an_error()
    {
        Assert.False(Parse("flowchart TD\n  A[x]\n  end", out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("no open subgraph", error!.Reason);
    }

    [Fact]
    public void Comments_are_ignored()
    {
        Assert.True(Parse("""
            flowchart TD
              %% a note to self
              A --> B %% and one on the same line
            """, out var chart, out var error), error?.ToString());

        Assert.Equal(2, chart.NodeCount);
        Assert.Equal(1, chart.EdgeCount);
    }

    [Fact]
    public void A_comment_marker_inside_a_quoted_label_is_not_a_comment()
    {
        // Otherwise a title containing %% is truncated and the diagram shows a
        // different title from the one written - the quietest kind of wrong.
        Assert.True(Parse("flowchart TD\n  A[\"100%% done\"] --> B", out var chart, out var error), error?.ToString());

        Assert.Equal("100%% done", chart.Find("A")!.Label);
    }

    [Fact]
    public void Appearance_statements_are_skipped_with_a_warning()
    {
        // `style` and `click` change how it looks, never what it means, so dropping
        // them cannot make the diagram wrong. The warning is there so nobody
        // wonders why their colours went missing.
        Assert.True(Parse("""
            flowchart TD
              A[x] --> B[y]
              style A fill:#f9f
              click A "https://example.com"
            """, out var chart, out var error), error?.ToString());

        Assert.Equal(2, chart.NodeCount);
        Assert.Equal(2, chart.Warnings.Count);
        Assert.All(chart.Warnings, warning => Assert.Contains("not drawn", warning));
    }

    [Fact]
    public void A_quoted_header_title_is_read()
    {
        Assert.True(Parse("flowchart LR \"How sync works\"", out var chart, out var error), error?.ToString());

        Assert.Equal(DiagramDirection.LeftRight, chart.Direction);
        Assert.Equal("How sync works", chart.Title);
    }

    [Fact]
    public void A_stray_word_after_the_direction_is_not_mistaken_for_a_title()
    {
        // Unquoted text after LR is a typo, not a caption. Guessing would put
        // nonsense in the corner of the diagram.
        Assert.True(Parse("flowchart LR oops\n A --> B", out var chart, out var error), error?.ToString());

        Assert.Null(chart.Title);
    }

    [Theory]
    [InlineData("sequenceDiagram\n  Alice->>John: Hello")]
    [InlineData("classDiagram\n  Animal <|-- Duck")]
    [InlineData("gantt\n  title A schedule")]
    [InlineData("pie title Pets\n  \"Dogs\" : 10")]
    public void A_diagram_of_another_kind_is_refused_with_a_reason(string source)
    {
        // The important behaviour: refuse, and say why. Falling back to a code
        // block is honest; rendering it as an empty flowchart would not be.
        Assert.False(Parse(source, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("flowcharts are drawn", error!.Reason);
        Assert.Contains("not supported yet", error.Reason);
    }

    [Fact]
    public void An_empty_fence_is_refused_rather_than_drawn_blank()
    {
        Assert.False(Parse("   \n  \n", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Null_input_is_refused_rather_than_throwing()
    {
        Assert.False(FlowchartParser.TryParse(null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Two_nodes_with_no_edge_between_them_is_an_error()
    {
        // `A B` is a typo for `A --> B`. Accepting it would draw two boxes and no
        // arrow, which is a diagram that does not say what was written.
        Assert.False(Parse("flowchart TD\n  A B", out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("arrow", error!.Reason);
    }

    [Fact]
    public void An_unclosed_quote_is_an_error()
    {
        Assert.False(Parse("flowchart TD\n  A[\"never closed] --> B", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void An_unclosed_shape_is_an_error()
    {
        Assert.False(Parse("flowchart TD\n  A[never closed --> B", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_parse_error_carries_the_line_it_happened_on()
    {
        // "Something is wrong with this diagram" is not actionable; "line 4" is.
        Assert.False(Parse("flowchart TD\n  A[x]\n  B[y]\n  A B", out _, out var error));

        Assert.Equal(4, error!.Line);
    }

    [Fact]
    public void Parsing_the_same_text_twice_gives_the_same_diagram()
    {
        const string source = "flowchart TD\n  A[a] --> B[b]\n  A --> C[c]\n  C --> D[d]\n  B --> D";

        Parse(source, out var first, out _);
        Parse(source, out var second, out _);

        Assert.Equal(
            first.Nodes.Select(node => (node.Id, node.Label, node.Shape)),
            second.Nodes.Select(node => (node.Id, node.Label, node.Shape)));

        Assert.Equal(
            first.Edges.Select(edge => (edge.FromId, edge.ToId, edge.Label, edge.Style)),
            second.Edges.Select(edge => (edge.FromId, edge.ToId, edge.Label, edge.Style)));
    }

    [Fact]
    public void Nodes_come_out_in_the_order_they_were_written()
    {
        // The diagram should read in the order it was authored, not in hash order.
        // That stability is what makes the picture jump-free across re-renders.
        Assert.True(Parse("flowchart LR\n  Z[z] --> M[m] --> A[a]", out var chart, out var error), error?.ToString());

        Assert.Equal(new[] { "Z", "M", "A" }, chart.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void A_cycle_parses_rather_than_hanging()
    {
        // The ranker is bounded (see FlowchartLayout), so a cycle must not be able
        // to make the layout oscillate. The parser's job here is just to accept it.
        Assert.True(Parse("flowchart LR\n  A --> B --> C --> A", out var chart, out var error), error?.ToString());

        Assert.Equal(3, chart.NodeCount);
        Assert.Equal(3, chart.EdgeCount);
    }

    // A wrapper so every test says the parser's own reason when it disagrees. The
    // raw TryParse returns a nullable error, which is not a string, and the
    // message is worth more than the ceremony.
    private static bool Parse(
        string source, out Flowchart flowchart, out FlowchartParseError? error) =>
        FlowchartParser.TryParse(source, out flowchart, out error);
}
