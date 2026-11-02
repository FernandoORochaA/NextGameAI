using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

public interface IGameEnrichmentService
{
    Task<GameEnrichmentResult> EnrichTaxonomiesAsync(int gameId, CancellationToken cancellationToken = default);
    Task<GameEnrichmentResult> EnrichDurationAndMultiplayerAsync(int gameId, CancellationToken cancellationToken = default);
}

public enum GameEnrichmentOutcome
{
    Applied, Partial, Preserved, GameNotFound, MissingIgdbId, IdentityMismatch, PersistenceFailure
}

public enum TaxonomyEnrichmentOutcome { Applied, EmptyPreserved, InvalidResponse, ExternalFailure, GameNotFound }

// No payloads or keyword names in operation results or exception messages.
public sealed record TaxonomyEnrichmentResult(CatalogMetadataSection Section, TaxonomyEnrichmentOutcome Outcome,
    IgdbMetadataStatus ExternalStatus, IgdbQueryFailure? Failure);

public sealed record GameEnrichmentResult(GameEnrichmentOutcome Outcome,
    IReadOnlyList<TaxonomyEnrichmentResult> Sections);
