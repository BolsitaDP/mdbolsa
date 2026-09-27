using System.Text;

namespace MdBolsa.Core.Diagrams;

/// <summary>
/// Why a fence could not be turned into a flowchart. The shell shows this as the
/// reason it fell back to showing the code, because "this did not render" with no
/// explanation is the most annoying thing a preview can do.
/// </summary>
public sealed record FlowchartParseError(string Reason, int Line)
{
    public override string ToString() => $"line {Line}: {Reason}";
}

/// <summary>
/// Parses the flowchart subset of Mermaid.
///
/// Hand-written, and the reason is in 0013: a preview pane is a closed set of
/// shapes, and a closed set is a set you can test. This parser has no dependency,
/// runs on every client, and returns a *failure* for anything it does not
/// understand rather than a best guess. That direction of travel is the whole
/// design. A flowchart that quietly dropped half its arrows would be a lie the
/// reader cannot detect, whereas a code block is obviously a code block.
///
/// Supported: the header and its direction, node shapes, quoted and unquoted
/// labels, the four edge styles, edge labels in both spellings, edge chains,
/// subgraphs, and comments. Ignored with a warning: `click`, `style`, `classDef`
/// and friends - they change appearance, never meaning, so dropping them cannot
/// make the diagram wrong.
/// </summary>
public static class FlowchartParser
{
    // Edge operators, longest first, so `-->` is not read as `---` and `-.->` is
    // not read as `-.-`. Each carries the style it implies.
    private static readonly (string Token, DiagramEdgeStyle Style)[] EdgeTokens =
    [
        ("-.->", DiagramEdgeStyle.Dotted),
        ("-.-", DiagramEdgeStyle.Dotted),
        ("==>", DiagramEdgeStyle.Thick),
        ("===", DiagramEdgeStyle.Thick),
        ("--x", DiagramEdgeStyle.Thick),
        ("--o", DiagramEdgeStyle.Thick),
        ("-->", DiagramEdgeStyle.Solid),
        ("---", DiagramEdgeStyle.Solid),
    ];

    /// <summary>
    /// Shape delimiters, longest first, so `[[` is not read as `[` and `[/` is
    /// read as a parallelogram rather than as a slash inside a rectangle.
    /// </summary>
    private static readonly (string Open, string Close, DiagramShape Shape)[] Shapes =
    [
        ("[[", "]]", DiagramShape.Subroutine),
        ("[(", ")]", DiagramShape.Cylinder),
        ("((", "))", DiagramShape.DoubleCircle),
        ("{{", "}}", DiagramShape.Hexagon),
        ("([", "])", DiagramShape.Stadium),
        ("[/", "/]", DiagramShape.Parallelogram),
        ("[\\", "\\]", DiagramShape.ParallelogramAlt),
        ("[/", "\\]", DiagramShape.Trapezoid),
        ("[\\", "/]", DiagramShape.TrapezoidAlt),
        ("[", "]", DiagramShape.Rectangle),
        ("(", ")", DiagramShape.Rounded),
        ("{", "}", DiagramShape.Diamond),
        (">", "]", DiagramShape.Asymmetric),
    ];

    // Statements that change appearance rather than structure. Recognised so they
    // can be skipped cleanly, and reported so the reader is not left wondering why
    // their colours went missing.
    private static readonly string[] IgnoredStatements =
        ["click", "style", "classdef", "class", "linkstyle", "accTitle", "accDescr"];

    public static bool TryParse(string? source, out Flowchart flowchart, out FlowchartParseError? error)
    {
        flowchart = Flowchart.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = new FlowchartParseError("There is no diagram here.", 1);
            return false;
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');
        var warnings = new List<string>();

        var header = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (StripComment(lines[i]).Trim().Length == 0) continue;
            header = i;
            break;
        }

        if (header < 0)
        {
            error = new FlowchartParseError("There is no diagram here.", 1);
            return false;
        }

        var (direction, title, recognised) = ReadHeader(StripComment(lines[header]).Trim());
        if (!recognised)
        {
            error = new FlowchartParseError(
                "Only flowcharts are drawn. sequenceDiagram, classDiagram, gantt and the " +
                "rest are not supported yet, so this is shown as code.", header + 1);
            return false;
        }

        // Insertion-ordered, so the diagram reads in the order it was written and
        // two runs of the same text give the same diagram. That is what makes the
        // tests assertable and the picture stable across re-renders.
        var nodes = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
        var order = new List<string>();
        var groupsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var edges = new List<DiagramEdge>();

        // A list, not a stack: the outermost group is at index 0, which is the
        // order a reader expects when a node belongs to nested subgraphs.
        var groups = new List<string>();

        for (var line = header + 1; line < lines.Length; line++)
        {
            var text = StripComment(lines[line]).Trim();
            if (text.Length == 0) continue;

            if (text.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (groups.Count == 0)
                {
                    error = new FlowchartParseError("`end` with no open subgraph.", line + 1);
                    return false;
                }

                groups.RemoveAt(groups.Count - 1);
                continue;
            }

            if (text.StartsWith("subgraph", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSubgraph(text, out var groupId, out var groupTitle))
                {
                    error = new FlowchartParseError(
                        $"This subgraph has no name: `{Truncate(text)}`", line + 1);
                    return false;
                }

                EnsureNode(nodes, order, groupsOf, groupId, groupId, DiagramShape.Rectangle);

                if (groupTitle is not null && !groupTitle.Equals(groupId, StringComparison.Ordinal))
                {
                    // The title is a caption rather than the node's name, so it is
                    // kept as its own node and the subgraph id refers to the box.
                    EnsureNode(nodes, order, groupsOf, "sub:" + groupId, groupTitle, DiagramShape.Rectangle);
                }

                groups.Add(groupId);
                continue;
            }

            var ignored = IgnoredStatements.FirstOrDefault(word =>
                text.StartsWith(word, StringComparison.OrdinalIgnoreCase));
            if (ignored is not null)
            {
                warnings.Add(
                    $"Line {line + 1}: `{ignored}` is not drawn yet, so this diagram looks " +
                    "plainer than it was written.");
                continue;
            }

            if (!TryReadStatement(
                    text, line + 1, groups, nodes, order, groupsOf, edges, out error))
            {
                return false;
            }
        }

        if (groups.Count > 0)
        {
            error = new FlowchartParseError(
                $"The subgraph `{groups[^1]}` is never closed with `end`.", lines.Length);
            return false;
        }

        flowchart = new Flowchart(
            order.Select(id => nodes[id]).ToList(),
            edges,
            direction,
            title,
            warnings);
        return true;
    }

    // --- Header ------------------------------------------------------------

    private static (DiagramDirection Direction, string? Title, bool Recognised) ReadHeader(string line)
    {
        if (!line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("graph", StringComparison.OrdinalIgnoreCase))
        {
            return (DiagramDirection.TopDown, null, false);
        }

        var keyword = line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ? 9 : 5;
        var rest = line[keyword..].TrimStart();
        var direction = DiagramDirection.TopDown;

        foreach (var (word, value) in new (string, DiagramDirection)[]
                 {
                     ("TB", DiagramDirection.TopDown),
                     ("TD", DiagramDirection.TopDown),
                     ("BT", DiagramDirection.BottomUp),
                     ("LR", DiagramDirection.LeftRight),
                     ("RL", DiagramDirection.RightLeft),
                 })
        {
            if (!rest.StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;

            direction = value;
            rest = rest[word.Length..].TrimStart();
            break;
        }

        // A title is a title only if it is quoted. Unquoted text after the direction
        // is a typo, and guessing would put nonsense in the corner of the diagram.
        string? title = null;
        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
        {
            title = Unescape(rest[1..^1]);
        }

        return (direction, title, true);
    }

    private static bool TryReadSubgraph(string text, out string id, out string? title)
    {
        id = string.Empty;
        title = null;

        var rest = text["subgraph".Length..].Trim();

        // `subgraph one [Title]`, `subgraph "Title"`, or `subgraph one`.
        var bracket = rest.IndexOf('[');
        if (bracket >= 0)
        {
            var close = rest.IndexOf(']', bracket);
            title = close > bracket ? Unescape(rest[(bracket + 1)..close]) : null;
            id = rest[..bracket].Trim();
        }
        else if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
        {
            title = Unescape(rest[1..^1]);
            id = "sub" + title;
        }
        else
        {
            id = rest;
        }

        return id.Length > 0;
    }

    // --- Statements -------------------------------------------------------

    private readonly record struct NodeSpec(string Id, string Label, DiagramShape Shape);

    private static bool TryReadStatement(
        string text,
        int lineNumber,
        List<string> groups,
        Dictionary<string, DiagramNode> nodes,
        List<string> order,
        Dictionary<string, List<string>> groupsOf,
        List<DiagramEdge> edges,
        out FlowchartParseError? error)
    {
        error = null;
        var current = text;

        // A statement is a node, then zero or more (arrow, node) pairs - so
        // `A --> B --> C` is three nodes and two edges. Reading it as
        // "node, node, node" and expecting an arrow between each is the same thing
        // written the other way round, and it is the version that cannot tell a
        // missing arrow from a missing node.
        var parsed = ReadNodeSpec(current, out current);
        if (parsed is null)
        {
            error = new FlowchartParseError(
                $"Could not read this line as part of a flowchart: `{Truncate(text)}`", lineNumber);
            return false;
        }

        var previousId = AddNode(parsed.Value, groups, nodes, order, groupsOf);

        while (current.Trim().Length > 0)
        {
            var link = ReadEdge(current, out var afterEdge);
            if (link is null)
            {
                error = new FlowchartParseError(
                    $"Expected an arrow after `{previousId}`, but found `{Truncate(current.Trim())}`. " +
                    "Use `-->`, `---`, `-.->` or `==>`.", lineNumber);
                return false;
            }

            current = afterEdge;

            var next = ReadNodeSpec(current, out current);
            if (next is null)
            {
                error = new FlowchartParseError(
                    $"Expected a node after the arrow from `{previousId}`, but found " +
                    $"`{Truncate(current.Trim())}`.", lineNumber);
                return false;
            }

            var nodeId = AddNode(next.Value, groups, nodes, order, groupsOf);
            edges.Add(new DiagramEdge(previousId, nodeId, link.Value.Label, link.Value.Style));
            previousId = nodeId;
        }

        return true;
    }

    /// <summary>
    /// Records a node and returns the id it was stored under. A quoted node with no
    /// id of its own - which is legal in Mermaid, and is what a second mention of
    /// the same label attaches to - gets a stable id derived from the label, so the
    /// two mentions land on the same box instead of two.
    /// </summary>
    private static string AddNode(
        NodeSpec spec,
        List<string> groups,
        Dictionary<string, DiagramNode> nodes,
        List<string> order,
        Dictionary<string, List<string>> groupsOf)
    {
        var id = spec.Id.Length > 0
            ? spec.Id
            : "label:" + spec.Label.GetHashCode(StringComparison.Ordinal).ToString("x");

        // Group membership first, because EnsureNode hands the node the *current*
        // list for its id. Registering afterwards leaves the node holding a
        // different, empty list than the one the group was added to - which is how a
        // node inside a subgraph ends up with no subgraph.
        if (groups.Count > 0) AddGroup(groupsOf, id, groups);

        EnsureNode(nodes, order, groupsOf, id, spec.Label, spec.Shape);
        return id;
    }

    /// <summary>
    /// Reads one node: an id, an optional shape, and the label inside it.
    ///
    /// Two things this gets right that a naive reader does not:
    ///
    /// * **Nested delimiters.** `A[what if [this] happens?]` is one label, so the
    ///   close is found by counting depth rather than at the first `]`. Read
    ///   naively it is "what if [this" followed by junk - a diagram that is quietly
    ///   wrong, which is the failure mode this parser refuses to have anywhere.
    /// * **Quoted labels still close their shape.** In `A["text"]` the quote ends
    ///   the *label* and the `]` ends the *shape*, and both have to be consumed.
    ///   Inside the quotes a doubled `""` is one quote, so the closing quote is
    ///   found by skipping the pairs rather than at the first `"` - otherwise
    ///   `A["say ""hello"""]` is read as the label `say "`.
    /// </summary>
    private static NodeSpec? ReadNodeSpec(string text, out string after)
    {
        text = text.TrimStart();

        var id = ReadIdentifier(text);
        if (id.Length == 0)
        {
            after = text;
            return null;
        }

        var rest = text[id.Length..].TrimStart();

        // Longest open wins, so `[[` is not read as `[`. Then, among the shapes that
        // share that open, the one whose *close* actually matches decides it -
        // `[/` opens both a parallelogram (closed `/]`) and a trapezoid (closed
        // `\]`), and the only way to tell them apart is to look at what comes
        // after the label. Picking the first candidate blind gets half of them
        // wrong and fails to parse the other half.
        // Candidates are the shapes whose opening delimiter is the longest one that
        // actually matches here. Both conditions matter: matching the longest open
        // alone would let `A[/x\]` be tried as a `[` rectangle, and matching the
        // open alone would let `[` beat `[[`.
        var candidates = Shapes
            .Where(candidate => Matches(rest, 0, candidate.Open))
            .OrderByDescending(candidate => candidate.Open.Length)
            .ToList();

        var openLength = candidates.Count == 0 ? 0 : candidates[0].Open.Length;
        candidates = candidates
            .Where(candidate => candidate.Open.Length == openLength)
            .ToList();

        if (openLength == 0)
        {
            // A bare id, or a quoted label with no shape around it.
            if (rest.Length >= 2 && rest[0] == '"')
            {
                var (label, index) = ReadQuoted(rest, 0);
                after = index < 0 ? text : rest[index..];
                return index < 0 ? null : new NodeSpec(id, label, DiagramShape.Rounded);
            }

            after = rest;
            return new NodeSpec(id, id, DiagramShape.Rectangle);
        }

        var body = rest[openLength..];

        if (body.StartsWith('"'))
        {
            var (label, index) = ReadQuoted(body, 0);
            if (index < 0)
            {
                after = text;
                return null;
            }

            var shape = candidates[0];

            // The shape's close is not optional. `A["text"` is a typo, and drawing
            // it anyway would swallow the next statement.
            if (!Matches(body, index, shape.Close))
            {
                after = text;
                return null;
            }

            after = body[(index + shape.Close.Length)..];
            return new NodeSpec(id, label, shape.Shape);
        }

        // Now the close decides: `[/` opens both a parallelogram and a trapezoid,
        // and the only thing that separates them is what follows the label.
        foreach (var candidate in candidates)
        {
            var end = FindClosing(body, candidate.Open, candidate.Close);
            if (end < 0) continue;

            after = body[(end + candidate.Close.Length)..];
            return new NodeSpec(id, Unescape(body[..end].Trim()), candidate.Shape);
        }

        after = text;
        return null;
    }

    /// <summary>
    /// Reads a quoted label starting at <paramref name="at"/>, which must be the
    /// opening quote. Returns the unescaped label and the index just past the
    /// closing quote. Returns (-1, at) if the quote is never closed.
    /// </summary>
    private static (string Label, int IndexAfter) ReadQuoted(string text, int at)
    {
        var label = new StringBuilder();
        var i = at + 1;

        while (i < text.Length)
        {
            if (text[i] != '"')
            {
                label.Append(text[i++]);
                continue;
            }

            // A doubled quote is one quote inside the label; a lone one ends it.
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                label.Append('"');
                i += 2;
                continue;
            }

            return (label.ToString().Trim(), i + 1);
        }

        return ("\0unterminated", -1);
    }

    /// <summary>
    /// Finds the close that matches an already-opened shape, so a label may contain
    /// its own delimiters. <paramref name="text"/> starts *inside* the shape - the
    /// opening delimiter has already been sliced off - which is why the depth
    /// starts at one.
    /// </summary>
    private static int FindClosing(string text, string open, string close)
    {
        var depth = 1;

        for (var i = 0; i < text.Length; i++)
        {
            if (Matches(text, i, open))
            {
                depth++;
                i += open.Length - 1;
                continue;
            }

            if (!Matches(text, i, close)) continue;

            depth--;
            if (depth == 0) return i;

            i += close.Length - 1;
        }

        return -1;
    }

    private static bool Matches(string text, int at, string token) =>
        at >= 0 && at + token.Length <= text.Length &&
        text.AsSpan(at, token.Length).SequenceEqual(token.AsSpan());

    /// <summary>
    /// Reads a node id. Letters, digits, underscore, and `-` or `.` *inside* the id -
    /// but never as the first character, because `-` is also how every edge starts.
    /// Allowing it here would read `A --&gt; B` as the node `A` followed by a node
    /// called `--`, and the error that produces ("expected an arrow between A and
    /// nothing") points nowhere near the cause.
    /// </summary>
    private static string ReadIdentifier(string text)
    {
        if (text.Length == 0) return string.Empty;
        if (!char.IsLetterOrDigit(text[0]) && text[0] != '_') return string.Empty;

        var i = 1;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.')) i++;
        return text[..i];
    }

    /// <summary>
    /// Reads the edge at the start of the text, in either spelling:
    /// `A --&gt;|yes| B` and `A -- yes --&gt; B` are the same edge.
    /// </summary>
    private static (string? Label, DiagramEdgeStyle Style)? ReadEdge(string text, out string after)
    {
        text = text.TrimStart();
        after = text;

        // `-- label -->` has to be recognised *before* the plain operators, because
        // the text in the middle means it does not start with `---` or `-->` at all.
        // Checking the tokens first would make this spelling an error, and it is one
        // of the two spellings Mermaid allows for a labelled edge.
        if (Matches(text, 0, "--") && !Matches(text, 0, "-->") && !Matches(text, 0, "---"))
        {
            var arrow = IndexOf(text, "-->", 2);
            var line = IndexOf(text, "---", 2);
            var at = arrow < 0 ? line : line < 0 ? arrow : Math.Min(arrow, line);

            if (at > 2)
            {
                after = text[(at + 3)..];
                return (Unescape(text[2..at].Trim()), DiagramEdgeStyle.Solid);
            }
        }

        var token = EdgeTokens.FirstOrDefault(pair => Matches(text, 0, pair.Token));
        if (token.Token is null) return null;

        var rest = text[token.Token.Length..];

        // `|label|` immediately after the operator.
        if (rest.StartsWith('|'))
        {
            var close = rest.IndexOf('|', 1);
            if (close > 0)
            {
                after = rest[(close + 1)..];
                return (Unescape(rest[1..close]), token.Style);
            }
        }

        after = rest;
        return (null, token.Style);
    }

    private static int IndexOf(string text, string token, int from) =>
        from >= text.Length ? -1 : text.IndexOf(token, from, StringComparison.Ordinal);

    // --- Helpers ----------------------------------------------------------

    private static void EnsureNode(
        Dictionary<string, DiagramNode> nodes,
        List<string> order,
        Dictionary<string, List<string>> groupsOf,
        string id,
        string label,
        DiagramShape shape)
    {
        if (nodes.TryGetValue(id, out var existing))
        {
            // A later mention can add a shape to a node introduced bare - `A` then
            // `A[Start]`. The shape wins, because it was the more specific claim.
            // The group list is taken from groupsOf on every mention, so a node first
            // written outside a subgraph and repeated inside it ends up in both.
            var membership = groupsOf.TryGetValue(id, out var current) ? current : existing.Groups;

            nodes[id] = existing with
            {
                Label = existing.Shape == DiagramShape.Rectangle && shape != DiagramShape.Rectangle
                    ? label
                    : existing.Label,
                Shape = existing.Shape == DiagramShape.Rectangle && shape != DiagramShape.Rectangle
                    ? shape
                    : existing.Shape,
                Groups = membership,
            };

            return;
        }

        nodes[id] = new DiagramNode(
            id, label, shape, groupsOf.TryGetValue(id, out var list) ? list : []);

        order.Add(id);
    }

    private static void AddGroup(
        Dictionary<string, List<string>> groupsOf, string nodeId, List<string> groups)
    {
        if (!groupsOf.TryGetValue(nodeId, out var list))
        {
            list = [];
            groupsOf[nodeId] = list;
        }

        foreach (var group in groups)
        {
            if (!list.Contains(group)) list.Add(group);
        }
    }

    /// <summary>
    /// Strips a `%%` comment, but not one inside a quoted label - Mermaid allows
    /// quotes in labels, and a title containing `%%` is a title, not a comment.
    /// </summary>
    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            if (!inQuotes && line[i] == '%' && line[i + 1] == '%') return line[..i];
        }

        return line;
    }

    private static string Unescape(string raw) =>
        raw.Replace("\"\"", "\"", StringComparison.Ordinal).Trim();

    private static string Truncate(string text) =>
        text.Length <= 40 ? text : text[..37] + "...";
}
