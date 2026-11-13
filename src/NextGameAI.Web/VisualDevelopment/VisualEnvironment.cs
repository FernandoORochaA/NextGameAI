using Microsoft.Extensions.Http;

namespace NextGameAI.Web.VisualDevelopment;

public static class VisualEnvironment
{
    public const string Name = "VisualDevelopment";

    // Called BEFORE CreateBuilder so a conflicting protected profile cannot load Development secrets.
    public static void ValidateLaunch(string[] args, string? profile, string? dotnetEnvironment, string? aspnetEnvironment)
    {
        if (profile is null) return; // Normal profiles keep their current behavior.
        var requested = new ConfigurationBuilder().AddCommandLine(args).Build()["environment"];
        if (profile != "1" || dotnetEnvironment != Name || aspnetEnvironment != Name || requested is not null && requested != Name)
            throw new InvalidOperationException("O perfil visual exige o ambiente VisualDevelopment, sem sobrescritas incompatíveis.");
    }

    public static VisualDatabaseGuard Configure(string environment, IConfiguration configuration, string localApplicationData)
    {
        if (environment != Name || configuration["VisualDevelopment:ConfigurationVersion"] != "1" ||
            !string.Equals(configuration["Igdb:Enabled"], "false", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(configuration["Igdb:ClientId"]) || !string.IsNullOrEmpty(configuration["Igdb:ClientSecret"]))
            throw new InvalidOperationException("A configuração visual é obrigatória, sem integração real ou credenciais.");
        var guard = new VisualDatabaseGuard(localApplicationData);
        if (configuration.GetConnectionString("Library") is { } supplied) guard.ValidateConnectionString(supplied);
        guard.RequirePrepared();
        return guard;
    }

    public static void AddHttpBarrier(IServiceCollection services) =>
        services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, VisualHttpBarrier>());
}

// Applies to current IHttpClientFactory clients, including named Twitch and typed IGDB.
// Does not claim to intercept manually constructed HttpClient or browser traffic.
public sealed class VisualHttpBarrier : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.PrimaryHandler = new BlockedVisualHttpHandler();
        builder.AdditionalHandlers.Clear();
    };
}

public sealed class BlockedVisualHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("HTTP externo bloqueado no ambiente visual. Utilize apenas dados sintéticos.");
}
