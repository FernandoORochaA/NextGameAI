using Microsoft.Extensions.DependencyInjection.Extensions;
using NextGameAI.Web.Configuration;

namespace NextGameAI.Web.Services.Igdb;

public static class IgdbServiceCollectionExtensions
{
    public static IServiceCollection AddIgdbAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // No startup validation or network call: the local library must work without credentials.
        services.AddOptions<IgdbOptions>().Bind(configuration.GetSection(IgdbOptions.SectionName));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHttpClient(IgdbTokenProvider.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // Per-operation linked timeout in the provider.
            client.MaxResponseContentBufferSize = 64 * 1024;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // Never forward the credential-bearing POST to a redirect target.
            UseCookies = false
        })
        .RemoveAllLoggers();
        services.AddSingleton<IIgdbTokenProvider, IgdbTokenProvider>();
        return services;
    }
}
