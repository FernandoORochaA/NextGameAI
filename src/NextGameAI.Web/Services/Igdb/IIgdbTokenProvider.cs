namespace NextGameAI.Web.Services.Igdb;

public interface IIgdbTokenProvider
{
    bool IsConfigured { get; }
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    // An old 401 must not invalidate a newer token obtained by another request.
    void Invalidate(string rejectedToken);
}
