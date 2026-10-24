using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

public sealed class IgdbSearchRelevanceTests
{
    private static IgdbGameResult Game(long id, string title, long? type = null, string? edition = null, long? parent = null) =>
        new(id, title, null, null, type, null, edition, parent);

    [Theory]
    [InlineData("Resident Evil 4", "Resident Evil 4", "Resident Evil 4 Bundle", 0)]
    [InlineData("Persona 5 Royal", "Persona 5 Royal", "Persona 5", 10)]
    [InlineData("Resident Evil 4 VR", "Resident Evil 4 VR", "Resident Evil 4", 8)]
    [InlineData("The Witcher 3", "The Witcher 3", "The Witcher 3 and Friends Bundle", 0)]
    [InlineData("Northern Realms Expansion", "Northern Realms Expansion", "Northern Realms", 2)]
    [InlineData("Space Pack", "Space Pack", "Space", 3)]
    public void SpecificTitleOutranksDefaultTypePreference(string query, string exact, string related, long type)
    {
        var games = IgdbSearchRelevance.Rank([Game(1, related, 0), Game(2, exact, type)], query);
        Assert.Equal(new long[] { 2, 1 }, games.Select(g => g.IgdbId));
    }

    [Theory]
    [InlineData("  AÇÃO   HEROICA ", "Ação Heroica")]
    [InlineData("acao heroica", "Ação Heroica")]
    [InlineData("Ação: Heroica", "ACAO HEROICA")]
    public void NormalizesCaseAccentsWhitespaceAndPunctuation(string query, string title)
    {
        var games = IgdbSearchRelevance.Rank([Game(1, "Ação Heroica Extra", 0), Game(2, title, 3)], query);
        Assert.Equal(2, games[0].IgdbId);
    }

    [Fact]
    public void TypePriorityIsExplicitAndUnknownTypesRemain()
    {
        long[] types = [3, 2, 1, 999, 4, 11, 10, 9, 8, 0];
        var results = IgdbSearchRelevance.Rank(types.Select(t => Game(t + 1, "Sky Quest", t)), "Sky Quest");
        Assert.Equal(new long?[] { 0, 8, 9, 10, 11, 4, 999, 2, 1, 3 }, results.Select(g => g.GameTypeId));
    }

    [Fact]
    public void EditionMetadataMatchesExplicitEditionQueryAndKeepsParent()
    {
        var edition = Game(2, "Silver Journey", 9, "Deluxe Edition", 1);
        var results = IgdbSearchRelevance.Rank([Game(1, "Silver Journey", 0), edition], "Silver Journey Deluxe Edition");
        Assert.Same(edition, results[0]);
        Assert.Equal(1, results[0].VersionParentIgdbId);
    }

    [Fact]
    public void FullWordMatchAndPrefixOutrankPartiallyRelatedTitles()
    {
        var results = IgdbSearchRelevance.Rank([
            Game(1, "Ocean Adventure", 0), Game(2, "Legends Ocean Quest", 0),
            Game(3, "Ocean Quest Legends", 0)], "Ocean Quest");
        Assert.Equal(new long[] { 3, 2, 1 }, results.Select(g => g.IgdbId));
    }

    [Fact]
    public void TiesKeepExternalOrderRegardlessOfYearAndDistinctIds()
    {
        var first = Game(8, "Twin Worlds", 8) with { FirstReleaseDate = new DateOnly(2024, 1, 1) };
        var second = Game(3, "Twin Worlds", 8) with { FirstReleaseDate = new DateOnly(2001, 1, 1) };
        var results = IgdbSearchRelevance.Rank([first, second], "Twin Worlds");
        Assert.Equal(new long[] { 8, 3 }, results.Select(g => g.IgdbId));
    }

    [Fact]
    public void MissingMetadataDoesNotDropResultsAndDoesNotMutateCandidates()
    {
        var input = new[] { Game(7, "Cloud Song Extra"), Game(4, "Cloud Song") };
        var results = IgdbSearchRelevance.Rank(input, "Cloud Song");
        Assert.Equal(new long[] { 4, 7 }, results.Select(g => g.IgdbId));
        Assert.Equal(7, input[0].IgdbId); Assert.Same(input[1], results[0]);
    }

    [Fact]
    public void RanksBeforeTakingTen()
    {
        var input = Enumerable.Range(1, 29).Select(id => Game(id, "Orbit Bundle " + id, 3))
            .Append(Game(30, "Orbit", 0));
        var results = IgdbSearchRelevance.Rank(input, "Orbit");
        Assert.Equal(10, results.Count); Assert.Equal(30, results[0].IgdbId);
    }
}
