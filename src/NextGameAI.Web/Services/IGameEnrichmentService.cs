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
    Applied, Partial, Preserved, GameNotFound, MissingIgdbId, IdentityMismatch, PersistenceFailure, CommitIndeterminate
}

public enum TaxonomyEnrichmentOutcome { Applied, EmptyPreserved, InvalidResponse, ExternalFailure, GameNotFound }

// No payloads or keyword names in operation results or exception messages.
public sealed record TaxonomyEnrichmentResult(CatalogMetadataSection Section, TaxonomyEnrichmentOutcome Outcome,
    IgdbMetadataStatus ExternalStatus, IgdbQueryFailure? Failure,
    System.Net.HttpStatusCode? StatusCode = null, TimeSpan? RetryAfter = null);

public sealed record GameEnrichmentResult(GameEnrichmentOutcome Outcome,
    IReadOnlyList<TaxonomyEnrichmentResult> Sections)
{
    public IReadOnlyList<GameEnrichmentSectionDecision> Decisions { get; init; } = Array.Empty<GameEnrichmentSectionDecision>();
}

public enum GameEnrichmentDisposition
{
    Recent, CooldownActive, TemporalInconsistency, Updated, DiscardedAfterRevalidation,
    PersistenceFailure, ExternalFailure, NotRequested, EmptyPreserved, InvalidResponse, GameNotFound, CommitIndeterminate
}

public sealed record GameEnrichmentSectionDecision(CatalogMetadataSection Section,
    GameEnrichmentDisposition Disposition, DateTime? NextAllowedAtUtc = null);
