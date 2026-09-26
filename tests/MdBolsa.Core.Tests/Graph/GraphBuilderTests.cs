using MdBolsa.Core.Graph;
using MdBolsa.Core.Links;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Graph;

public class GraphBuilderTests
{
    private readonly Guid _welcomeId = Guid.NewGuid();
    private readonly Guid _homeId = Guid.NewGuid();
    private readonly Guid _dockerId = Guid.NewGuid();

    [Fact]
    public void BuildGlobal_IncludesEveryNoteAsANode()
    {
        var graph = Builder().BuildGlobal();

        var noteKeys = graph.Nodes.Where(n => n.Kind == GraphNodeKind.Note).Select(n => n.Key).ToList();
        Assert.Equal(3, noteKeys.Count);
        Assert.Contains($"note:{_dockerId}", noteKeys);
        Assert.Contains($"note:{_homeId}", noteKeys);
        Assert.Contains($"note:{_welcomeId}", noteKeys);
    }

    [Fact]
    public void BuildGlobal_AddsAnEdgePerResolvedLink()
    {
        var graph = Builder(links:
        [
            L(_welcomeId, "Personal/Home", _homeId),
            L(_homeId, "Welcome", _welcomeId),
        ]).BuildGlobal();

        var linkEdges = graph.Edges.Where(e => e.Kind == GraphEdgeKind.Link).ToList();
        Assert.Equal(2, linkEdges.Count);
        Assert.Contains(linkEdges, e => e.SourceKey == $"note:{_welcomeId}" && e.TargetKey == $"note:{_homeId}");
    }

    [Fact]
    public void BuildGlobal_CountsUnresolvedLinks_WithoutAddingNodes()
    {
        var graph = Builder(links:
        [
            L(_welcomeId, "Personal/Home", _homeId),
            L(_welcomeId, "Nowhere", null),
        ]).BuildGlobal();

        Assert.Equal(1, graph.UnresolvedLinkCount);
        Assert.Equal(3, graph.NodeCount);
    }

    [Fact]
    public void BuildGlobal_SkipsSelfLinks()
    {
        var graph = Builder(links: [L(_welcomeId, "Welcome", _welcomeId)]).BuildGlobal();

        Assert.Empty(graph.Edges);
        Assert.Equal(0, graph.UnresolvedLinkCount);
    }

    [Fact]
    public void BuildGlobal_DedupesDifferentLinkTextsResolvingToTheSameTarget()
    {
        var graph = Builder(links:
        [
            L(_welcomeId, "Personal/Home", _homeId),
            L(_welcomeId, "Home", _homeId),
        ]).BuildGlobal();

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void BuildGlobal_IncludesTagNodesAndNoteToTagEdges()
    {
        var graph = Builder(tagsByNote: new Dictionary<Guid, string[]> { [_dockerId] = ["homelab", "docker"] })
            .BuildGlobal();

        Assert.Equal(["docker", "homelab"], graph.Nodes.Where(n => n.Kind == GraphNodeKind.Tag).Select(n => n.Label));
        Assert.Equal(2, graph.Edges.Count(e => e.Kind == GraphEdgeKind.Tag));
        Assert.All(graph.Edges.Where(e => e.Kind == GraphEdgeKind.Tag),
            e => Assert.StartsWith("tag:", e.TargetKey, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLocal_AtDepthOne_IncludesDirectLinksAndBacklinks()
    {
        // Around welcome: home is what welcome links out to, docker is what links
        // in. Both are one hop, so both belong in the note-local graph.
        var graph = Builder(links:
        [
            L(_welcomeId, "Personal/Home", _homeId),
            L(_homeId, "Welcome", _welcomeId),
            L(_dockerId, "Welcome", _welcomeId),
        ]).BuildLocal(_welcomeId, depth: 1);

        var keys = graph.Nodes.Select(n => n.Key).ToList();
        Assert.Equal(3, keys.Count);
        Assert.Contains($"note:{_welcomeId}", keys);
        Assert.Contains($"note:{_homeId}", keys);
        Assert.Contains($"note:{_dockerId}", keys);
    }

    [Fact]
    public void BuildLocal_ExcludesNotesBeyondTheRequestedDepth()
    {
        // docker -> welcome -> home: at depth 1 around docker, home is two hops out.
        var builder = Builder(links:
        [
            L(_dockerId, "Welcome", _welcomeId),
            L(_welcomeId, "Personal/Home", _homeId),
        ]);

        var atDepthOne = builder.BuildLocal(_dockerId, depth: 1);
        var atDepthTwo = builder.BuildLocal(_dockerId, depth: 2);

        Assert.Equal(2, atDepthOne.NodeCount);
        Assert.DoesNotContain(atDepthOne.Nodes, n => n.Key == $"note:{_homeId}");
        Assert.Equal(3, atDepthTwo.NodeCount);
        Assert.Contains(atDepthTwo.Nodes, n => n.Key == $"note:{_homeId}");
    }

    [Fact]
    public void BuildLocal_ExpandsFurtherWithMoreDepth()
    {
        var graph = Builder(links:
        [
            L(_dockerId, "Welcome", _welcomeId),
            L(_welcomeId, "Personal/Home", _homeId),
        ]).BuildLocal(_welcomeId, depth: 2);

        Assert.Equal(3, graph.NodeCount);
    }

    [Fact]
    public void BuildLocal_ExpandsThroughTagNodes()
    {
        // home and docker share the homelab tag but don't link to each other: at
        // depth 2 the tag node bridges them.
        var sharedTags = new Dictionary<Guid, string[]> { [_homeId] = ["homelab"], [_dockerId] = ["homelab"] };

        var atDepthOne = Builder(tagsByNote: sharedTags).BuildLocal(_homeId, depth: 1);
        var atDepthTwo = Builder(tagsByNote: sharedTags).BuildLocal(_homeId, depth: 2);

        // Depth 1: home and the tag it points at. Depth 2: the tag's other note.
        Assert.Equal(2, atDepthOne.NodeCount);
        Assert.Equal(3, atDepthTwo.NodeCount);
        Assert.Contains(atDepthTwo.Nodes, n => n.Key == "tag:homelab");
        Assert.Contains(atDepthTwo.Nodes, n => n.Key == $"note:{_dockerId}");
    }

    [Fact]
    public void BuildLocal_KeepsOnlyEdgesBetweenReachedNodes()
    {
        var graph = Builder(links:
        [
            L(_dockerId, "Welcome", _welcomeId),
            L(_welcomeId, "Personal/Home", _homeId),
        ]).BuildLocal(_dockerId, depth: 1);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal($"note:{_dockerId}", edge.SourceKey);
        Assert.Equal($"note:{_welcomeId}", edge.TargetKey);
    }

    [Fact]
    public void BuildLocal_ClampsDepth()
    {
        Assert.Equal(2, Builder(links: [L(_dockerId, "Welcome", _welcomeId)]).BuildLocal(_dockerId, depth: 0).NodeCount);
        Assert.Equal(2, Builder(links: [L(_dockerId, "Welcome", _welcomeId)]).BuildLocal(_dockerId, depth: 99).NodeCount);
    }

    [Fact]
    public void BuildLocal_ReturnsOnlyTheCenter_ForAnIsolatedNote()
    {
        var graph = Builder().BuildLocal(_dockerId, depth: 2);

        Assert.Equal($"note:{_dockerId}", Assert.Single(graph.Nodes).Key);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void BuildLocal_ReturnsEmpty_ForAnUnknownNote()
    {
        var graph = Builder().BuildLocal(Guid.NewGuid(), depth: 2);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void BuildLocal_CarriesTheUnresolvedLinkCount_Through()
    {
        var graph = Builder(links: [L(_dockerId, "Nowhere", null)]).BuildLocal(_dockerId, depth: 1);

        Assert.Equal(1, graph.UnresolvedLinkCount);
    }

    [Fact]
    public void GraphModel_ReportsDegree()
    {
        var graph = Builder(
            links: [L(_welcomeId, "Personal/Home", _homeId)],
            tagsByNote: new Dictionary<Guid, string[]> { [_homeId] = ["homelab"] }).BuildGlobal();

        Assert.Equal(2, graph.DegreeOf($"note:{_homeId}"));
        Assert.Equal(1, graph.DegreeOf("tag:homelab"));
        Assert.NotNull(graph.Find($"note:{_welcomeId}"));
    }

    [Fact]
    public void TryGetNoteId_OnlyUnwrapsNoteKeys()
    {
        Assert.True(GraphNode.TryGetNoteId($"note:{_welcomeId}", out var id));
        Assert.Equal(_welcomeId, id);
        Assert.False(GraphNode.TryGetNoteId("tag:homelab", out _));
        Assert.False(GraphNode.TryGetNoteId($"tag:{_welcomeId}", out _));
    }

    private static NoteLink L(Guid source, string targetText, Guid? targetNoteId) =>
        new(source, targetText, targetNoteId);

    private GraphBuilder Builder(
        NoteLink[]? links = null,
        IReadOnlyDictionary<Guid, string[]>? tagsByNote = null) =>
        new(
            new FakeVaultIndex(
                Note(_welcomeId, "Welcome.md"),
                Note(_homeId, "Personal/Home.md"),
                Note(_dockerId, "Development/Docker.md")),
            new FakeLinkIndex(links ?? []),
            new FakeTagIndex(tagsByNote ?? new Dictionary<Guid, string[]>()));

    private static NoteMetadata Note(Guid id, string relativePath) =>
        new(id, relativePath, Path.GetFileNameWithoutExtension(relativePath), "hash", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeVaultIndex(params NoteMetadata[] notes) : IVaultIndex
    {
        public IReadOnlyList<NoteMetadata> GetAll() => notes;

        public void Upsert(NoteMetadata note) => throw new NotSupportedException();

        public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent) => throw new NotSupportedException();
    }

    private sealed class FakeLinkIndex(IEnumerable<NoteLink> links) : ILinkIndex
    {
        public IReadOnlyList<NoteLink> GetAll() => links.ToList();

        public void ReplaceLinksForNote(Guid sourceNoteId, IReadOnlyList<NoteLink> links) =>
            throw new NotSupportedException();

        public IReadOnlyList<Guid> GetBacklinkSourceIds(Guid targetNoteId) => throw new NotSupportedException();

        public int DeleteLinksForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTagIndex(IReadOnlyDictionary<Guid, string[]> tagsByNote) : ITagIndex
    {
        public IReadOnlyList<TagCount> GetTagCounts() => throw new NotSupportedException();

        public IReadOnlyList<string> GetTagsForNote(Guid noteId) =>
            tagsByNote.TryGetValue(noteId, out var tags) ? tags : [];

        public IReadOnlyList<Guid> GetNoteIdsForTag(string tag) => throw new NotSupportedException();

        public void ReplaceTagsForNote(Guid noteId, IReadOnlyList<string> tags) =>
            throw new NotSupportedException();

        public int DeleteTagsForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent) =>
            throw new NotSupportedException();
    }
}
