using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;

namespace NextGameAI.VisualSetup;

// Positive taxonomy/platform external IDs below are synthetic placeholders, NOT reserved IGDB IDs.
// Games deliberately have no IGDB identity, and no cover or external URL is inserted.
internal sealed record DemoData(Game[] Games, LibraryEntry[] Entries, GameOwnership[] Ownerships,
    CatalogTaxonomy[] Taxonomies, GameTaxonomy[] Links, GameTimeToBeat[] Times, CatalogPlatform[] CatalogPlatforms,
    GameMultiplayerMode[] Multiplayer, GameEnrichmentState[] States, UserProfile[] Profiles, Store[] Stores, Platform[] Platforms);

internal static class VisualDemo
{
    private const string Source = "VisualSynthetic";
    internal static DemoData Create(DateTime t)
    {
        string[] titles = ["Visual: Expedição Aurora", "Visual: Oficina Orbital", "Visual: Jardim de Papel", "Visual: Caminhos de Névoa"];
        var games = titles.Select((title, i) => new Game { Id = 1001 + i, Title = title }).ToArray();
        int?[] ratings = [9, 0, null, 6];
        LibraryStatus[] statuses = [LibraryStatus.Playing, LibraryStatus.Completed, LibraryStatus.NotStarted, LibraryStatus.Abandoned];
        var entries = games.Select((game, i) => new LibraryEntry { Id = 1001 + i, GameId = game.Id, UserProfileId = 1,
            Status = statuses[i], Rating = ratings[i], Notes = "Observação inteiramente fictícia de demonstração.",
            AbandonmentReason = i == 3 ? "Motivo fictício de abandono." : null, AddedAtUtc = t, UpdatedAtUtc = t }).ToArray();
        var owners = new[] { (1001, 1), (1001, 3), (1002, 6), (1003, 4), (1004, 1) }
            .Select((pair, i) => new GameOwnership { Id = 1001 + i, LibraryEntryId = pair.Item1, PlatformId = pair.Item2, StoreId = 1001 }).ToArray();
        string[] names = ["Exploração fictícia", "Tema sintético", "Modo demonstrativo", "Câmera demonstrativa", "SEGREDO_VISUAL_SINTETICO"];
        var taxonomies = names.Select((name, i) => new CatalogTaxonomy { Id = 1001 + i, Kind = (CatalogTaxonomyKind)i,
            IgdbId = 90000001 + i, Name = name }).ToArray();
        var links = taxonomies.Select(x => new GameTaxonomy { GameId = 1001, CatalogTaxonomyId = x.Id })
            .Concat(new[] { new GameTaxonomy { GameId = 1002, CatalogTaxonomyId = 1001 },
                new GameTaxonomy { GameId = 1004, CatalogTaxonomyId = 1001 } }).ToArray();
        var times = new[] {
            new GameTimeToBeat { GameId = 1001, HastilySeconds = 600, NormallySeconds = 3600, CompletelySeconds = 5400, SubmissionCount = 12, Source = Source },
            new GameTimeToBeat { GameId = 1002, NormallySeconds = 0, Source = Source } };
        var platforms = new[] { new CatalogPlatform { Id = 1001, IgdbId = 90000101, Name = "Console fictício Lumen" },
            new CatalogPlatform { Id = 1002, IgdbId = 90000102, Name = "Computador fictício Atlas" } };
        var multiplayer = new[] {
            new GameMultiplayerMode { Id = 1001, GameId = 1001, CatalogPlatformId = 1001, CampaignCoop = true,
                DropIn = false, OnlineCoop = true, OnlineCoopMax = 4, Source = Source },
            new GameMultiplayerMode { Id = 1002, GameId = 1001, CatalogPlatformId = 1002, OfflineCoop = true,
                OfflineCoopMax = 2, SplitScreen = false, Source = Source },
            new GameMultiplayerMode { Id = 1003, GameId = 1002, CampaignCoop = true, DropIn = false,
                OfflineMax = 0, Source = Source } };
        var states = Enum.GetValues<CatalogMetadataSection>().Select(section => new GameEnrichmentState
            { GameId = 1001, Section = section, LastResult = CatalogUpdateResult.Success, LastAttemptAtUtc = t.AddDays(-2),
                LastSuccessAtUtc = t.AddDays(-2), Source = Source })
            .Concat(new[] { CatalogMetadataSection.Genres, CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer }
                .Select(section => new GameEnrichmentState { GameId = 1002, Section = section,
                    LastResult = CatalogUpdateResult.Success, LastAttemptAtUtc = t.AddDays(-2), LastSuccessAtUtc = t.AddDays(-2), Source = Source }))
            .Concat(new[] {
                new GameEnrichmentState { GameId = 1002, Section = CatalogMetadataSection.Themes, LastResult = CatalogUpdateResult.TemporaryFailure,
                    LastAttemptAtUtc = t.AddDays(-1), Source = Source },
                new GameEnrichmentState { GameId = 1004, Section = CatalogMetadataSection.Genres, LastResult = CatalogUpdateResult.TemporaryFailure,
                    LastAttemptAtUtc = t.AddDays(-1), LastSuccessAtUtc = t.AddDays(-45), Source = Source } }).ToArray();
        (string, string)[] storeNames = [("steam", "Steam"), ("gog", "GOG"), ("epic", "Epic Games Store"),
            ("playstation-store", "PlayStation Store"), ("microsoft-store", "Microsoft Store"), ("nintendo-eshop", "Nintendo eShop"), ("physical", "Mídia física")];
        var stores = storeNames.Select((s, i) => new Store { Id = i + 1, Code = s.Item1, Name = s.Item2 })
            .Append(new Store { Id = 1001, Code = "visual-demo", Name = "Loja fictícia de demonstração" }).ToArray();
        (string, string)[] personalPlatforms = [("pc", "PC"), ("ps4", "PlayStation 4"), ("ps5", "PlayStation 5"),
            ("xbox-one", "Xbox One"), ("xbox-series", "Xbox Series X/S"), ("switch", "Nintendo Switch")];
        return new(games, entries, owners, taxonomies, links, times, platforms, multiplayer,
            states.OrderBy(s => s.GameId).ThenBy(s => s.Section).ToArray(),
            [new UserProfile { Id = 1, DisplayName = "Usuário local" }], stores,
            personalPlatforms.Select((p, i) => new Platform { Id = i + 1, Code = p.Item1, Name = p.Item2 }).ToArray());
    }

    internal static void Add(NextGameAiDbContext db, DemoData data)
    {
        db.Games.AddRange(data.Games); db.LibraryEntries.AddRange(data.Entries); db.GameOwnerships.AddRange(data.Ownerships);
        db.CatalogTaxonomies.AddRange(data.Taxonomies); db.GameTaxonomies.AddRange(data.Links);
        db.GameTimeToBeats.AddRange(data.Times); db.CatalogPlatforms.AddRange(data.CatalogPlatforms);
        db.GameMultiplayerModes.AddRange(data.Multiplayer); db.GameEnrichmentStates.AddRange(data.States);
        db.Stores.Add(data.Stores.Single(s => s.Id == 1001));
    }

    internal static string Serialize(DemoData data) => JsonSerializer.Serialize(data);

    internal static async Task<string> ReadSnapshotAsync(NextGameAiDbContext db, CancellationToken ct)
    {
        var entries = await db.LibraryEntries.AsNoTracking().OrderBy(e => e.Id).ToArrayAsync(ct);
        foreach (var e in entries) { e.AddedAtUtc = Utc(e.AddedAtUtc); e.UpdatedAtUtc = Utc(e.UpdatedAtUtc); }
        var states = await db.GameEnrichmentStates.AsNoTracking().OrderBy(s => s.GameId).ThenBy(s => s.Section).ToArrayAsync(ct);
        foreach (var s in states) { s.LastAttemptAtUtc = Utc(s.LastAttemptAtUtc); if (s.LastSuccessAtUtc.HasValue) s.LastSuccessAtUtc = Utc(s.LastSuccessAtUtc.Value); }
        return Serialize(new(
            await db.Games.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct), entries,
            await db.GameOwnerships.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct),
            await db.CatalogTaxonomies.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct),
            await db.GameTaxonomies.AsNoTracking().OrderBy(x => x.GameId).ThenBy(x => x.CatalogTaxonomyId).ToArrayAsync(ct),
            await db.GameTimeToBeats.AsNoTracking().OrderBy(x => x.GameId).ToArrayAsync(ct),
            await db.CatalogPlatforms.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct),
            await db.GameMultiplayerModes.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct), states,
            await db.UserProfiles.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct),
            await db.Stores.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct),
            await db.Platforms.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct)));
    }
    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);
}
