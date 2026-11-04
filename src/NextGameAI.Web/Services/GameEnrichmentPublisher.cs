namespace NextGameAI.Web.Services;

// Narrow backend boundary: publication only, no database, payloads or HTTP.
public interface IGameEnrichmentPublisher
{
    void Publish(EnrichmentCooldownObservation observation);
}

public sealed class GameEnrichmentPublisher(GameEnrichmentCooldown cooldown) : IGameEnrichmentPublisher
{
    public void Publish(EnrichmentCooldownObservation observation) => cooldown.RecordConfirmed(observation);
}
