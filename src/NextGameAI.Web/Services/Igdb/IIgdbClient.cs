namespace NextGameAI.Web.Services.Igdb;

public interface IIgdbClient
{
    Task<IReadOnlyList<IgdbGameResult>> SearchAsync(string term, CancellationToken cancellationToken = default);
    Task<IgdbGameResult?> GetByIdAsync(long igdbId, CancellationToken cancellationToken = default);
}
