using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.ViewModels;

public sealed record CatalogGameResult(long IgdbId, string Title, int? ReleaseYear,
    string? GameType, string? EditionTitle, string? CoverUrl)
{
    public static CatalogGameResult FromGame(Models.Game game) => FromGame(new IgdbGameResult(
        game.IgdbId ?? 0, game.Title, game.CoverImageId, game.FirstReleaseDate,
        game.GameTypeId, game.GameType, game.EditionTitle, null));

    public static CatalogGameResult FromGame(IgdbGameResult game) => new(game.IgdbId, game.Title,
        game.ReleaseYear, game.GameTypeId switch
        {
            0 => "Jogo principal", 3 => "Pacote", 5 => "Mod", 8 => "Remake",
            9 => "Remaster", 10 => "Versão expandida", 11 => "Adaptação", 14 => "Atualização",
            _ => game.GameType
        }, game.EditionTitle, IgdbCoverImages.GetUrl(game.CoverImageId, IgdbCoverSize.Big)?.AbsoluteUri);
}
