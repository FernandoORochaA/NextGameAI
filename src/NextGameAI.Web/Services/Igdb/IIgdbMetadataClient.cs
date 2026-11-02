namespace NextGameAI.Web.Services.Igdb;

public interface IIgdbMetadataClient
{
    // Invalid input throws IgdbQueryException; caller cancellation propagates OperationCanceledException.
    Task<IgdbMetadataResult> GetByIdAsync(long igdbId, CancellationToken cancellationToken = default);
}
