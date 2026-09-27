using MdBolsa.Core.Search;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Search;

// The quick switcher's brain. What matters is not that it finds things - it is
// that the thing it puts first is the one you meant, because the whole point is
// pressing Enter without reading.
public class NoteNameMatcherTests
{
    private static NoteMetadata Note(string relativePath) =>
        new(Guid.NewGuid(), relativePath, Path.GetFileNameWithoutExtension(relativePath),
            "hash", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static string Kind(string fileName, string query) =>
        NoteNameMatcher.Classify(Path.GetFileNameWithoutExtension(fileName), query)?.ToString() ?? "none";

    [Fact]
    public void AnExactNameIsAnExactMatch() =>
        Assert.Equal("Exact", Kind("Docker.md", "Docker"));

    [Fact]
    public void APrefixIsAPrefix() =>
        Assert.Equal("Prefix", Kind("Docker.md", "Doc"));

    [Fact]
    public void MatchingIgnoresCase() =>
        Assert.Equal("Prefix", Kind("docker.md", "DOC"));

    [Fact]
    public void AWordInsideTheNameIsAWordStart() =>
        Assert.Equal("WordStart", Kind("Notas de marzo.md", "marzo"));

    [Fact]
    public void AWordStartAfterADashIsAWordStart() =>
        Assert.Equal("WordStart", Kind("blog-postgres.md", "post"));

    [Fact]
    public void AWordStartAfterAnUnderscoreIsAWordStart() =>
        Assert.Equal("WordStart", Kind("deploy_production.md", "production"));

    [Fact]
    public void AWordStartAfterASpaceIsAWordStart() =>
        Assert.Equal("WordStart", Kind("release notes.md", "notes"));

    [Fact]
    public void AWordStartAfterADotIsAWordStart() =>
        Assert.Equal("WordStart", Kind("notes.v2 archive.md", "archive"));

    [Fact]
    public void AVersionNumberIsNotTreatedAsAFileExtension()
    {
        // GetFileNameWithoutExtension strips after the LAST dot, so "v1.2 notes.md"
        // keeps its version. Getting this wrong would make the switcher match on
        // "2 notes" instead of "notes".
        Assert.Equal("WordStart", Kind("v1.2 notes.md", "notes"));
    }

    [Fact]
    public void AMatchInTheMiddleOfAWordIsNotAWordStart() =>
        Assert.Equal("Anywhere", Kind("Docker.md", "ocker"));

    [Fact]
    public void ANameThatDoesNotContainTheQueryDoesNotMatch() =>
        Assert.Equal("none", Kind("Docker.md", "postgres"));

    [Fact]
    public void AnEmptyQueryMatchesNothing() =>
        Assert.Empty(NoteNameMatcher.Match([Note("Docker.md")], "   "));

    [Fact]
    public void MatchingFoldsAccents()
    {
        // "informacion" has to find "Información". Filenames in this app are written
        // without accents most of the time, so that is the spelling people type, and
        // a switcher that misses it is worse than no switcher.
        Assert.Equal("Prefix", Kind("Información del servidor.md", "informacion"));

        // The other direction, and the answer is Exact rather than Prefix: once
        // both sides are folded, "informacion" *is* the whole name. Folding decides
        // what matches, so it changes the kind of match too - which is correct, and
        // worth pinning down so nobody "fixes" it to Prefix later.
        Assert.Equal("Exact", Kind("informacion.md", "Información"));
    }

    [Fact]
    public void AccentsAreOnlyFoldedForMatching_NotForStorage()
    {
        // A note the matcher scored is not a note that was renamed. The relative
        // path in the result has to be the one the file actually has.
        var note = Note("Información del servidor.md");

        var match = Assert.Single(NoteNameMatcher.Match([note], "informacion"));

        Assert.Equal("Información del servidor.md", match.Note.RelativePath);
    }

    [Fact]
    public void MatchPutsExactBeforePrefixBeforeWordStartBeforeAnywhere()
    {
        // Four ways of matching one query, in the order they should rank.
        // "xmarathon" is the worst: the query only appears glued to another word.
        var notes = new[]
        {
            Note("Notes about the xmarathon.md"),
            Note("The marathon plan.md"),
            Note("Marathon notes.md"),
            Note("Marathon.md"),
        };

        var ranked = NoteNameMatcher.Match(notes, "marathon");

        Assert.Equal(
            ["Marathon", "Marathon notes", "The marathon plan", "Notes about the xmarathon"],
            ranked.Select(match => match.Note.RelativePath[..^3]));
    }

    [Fact]
    public void AShorterNameWinsWithinTheSameKind()
    {
        // Both start with "nota"; "Notas" is obviously the better guess than
        // "Notas de la reunion de marzo", and someone pressing Enter should get it.
        var notes = new[] { Note("Notas de la reunion de marzo.md"), Note("Notas.md") };

        var ranked = NoteNameMatcher.Match(notes, "nota");

        Assert.Equal("Notas", ranked[0].Note.RelativePath[..^3]);
    }

    [Fact]
    public void EveryWordOfAMultiWordQueryHasToMatchSomething()
    {
        var notes = new[]
        {
            Note("Notas de marzo.md"),
            Note("Notas de abril.md"),
            Note("Marzo.md"),
        };

        var ranked = NoteNameMatcher.Match(notes, "notas marzo");

        Assert.Single(ranked);
        Assert.Equal("Notas de marzo", ranked[0].Note.RelativePath[..^3]);
    }

    [Fact]
    public void AMultiWordQueryStillMatchesASingleWordName()
    {
        // "A word here" has the words; a query of "here" alone should still find it,
        // and the ranking must not depend on how many words the name happens to have.
        var ranked = NoteNameMatcher.Match([Note("A word here.md")], "here");

        Assert.Single(ranked);
        Assert.Equal("A word here", ranked[0].Note.RelativePath[..^3]);
    }

    [Fact]
    public void TheMatchedWordIsTheOneThatMatched()
    {
        var ranked = NoteNameMatcher.Match([Note("Notas de marzo.md")], "marzo");

        Assert.Equal("marzo", ranked[0].MatchedWord);
    }

    [Fact]
    public void MatchRespectsTheLimit()
    {
        var notes = Enumerable.Range(0, 30).Select(i => Note($"Nota {i}.md")).ToList();

        Assert.Equal(5, NoteNameMatcher.Match(notes, "nota", limit: 5).Count);
    }

    [Fact]
    public void TwoNotesWithTheSameTitleInDifferentFoldersBothMatch()
    {
        // The file name is the same, so both are exact matches and the tie is broken
        // by path - deterministically, not by whatever order the index returned.
        var notes = new[] { Note("Personal/Docker.md"), Note("Work/Docker.md") };

        var ranked = NoteNameMatcher.Match(notes, "docker");

        Assert.Equal(2, ranked.Count);
        Assert.Equal(
            ["Personal/Docker.md", "Work/Docker.md"],
            ranked.Select(match => match.Note.RelativePath));
    }
}
