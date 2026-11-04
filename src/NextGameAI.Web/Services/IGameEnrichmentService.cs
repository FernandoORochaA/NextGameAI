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
    Applied, Partial, Preserved, GameNotFound, MissingIgdbId, IdentityMismatch, PersistenceFailure, CommitIndeterminate,
    CommittedWithPublicationFailure, CommittedWithPostCommitFailure
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
    public int WriteAttempts { get; init; }
    public EnrichmentCommitState CommitState { get; init; }
    public EnrichmentPublicationState PublicationState { get; init; }
    public EnrichmentWriteFailure WriteFailure { get; init; }
    public bool CleanupCompleted { get; init; } = true;
    public IReadOnlyList<CatalogMetadataSection> PublishedSections { get; init; } = Array.Empty<CatalogMetadataSection>();
    public IReadOnlyList<CatalogMetadataSection> PublicationFailedSections { get; init; } = Array.Empty<CatalogMetadataSection>();
}

public enum EnrichmentCommitState { NotStarted, Confirmed, Indeterminate }
public enum EnrichmentPublicationState { NotAttempted, Complete, Partial, Failed }
public enum EnrichmentWriteFailure { None, Busy, Locked, ForeignKey, Check, Unique, Constraint, Unknown }

public enum GameEnrichmentDisposition
{
    Recent, CooldownActive, TemporalInconsistency, Updated, DiscardedAfterRevalidation,
    PersistenceFailure, ExternalFailure, NotRequested, EmptyPreserved, InvalidResponse, GameNotFound, CommitIndeterminate
}

public sealed record GameEnrichmentSectionDecision(CatalogMetadataSection Section,
    GameEnrichmentDisposition Disposition, DateTime? NextAllowedAtUtc = null);
