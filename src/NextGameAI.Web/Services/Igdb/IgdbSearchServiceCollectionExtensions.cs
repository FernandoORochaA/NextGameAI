using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NextGameAI.Web.Services.Igdb;

public static class IgdbSearchServiceCollectionExtensions
{
    public static IServiceCollection AddIgdbSearch(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IgdbQueryCache>();
        services.AddSingleton<IgdbRequestScheduler>();
        services.AddTransient<IIgdbClient, IgdbClient>();
        services.AddTransient<IIgdbMetadataClient, IgdbMetadataClient>();
        services.AddHttpClient<IgdbApiTransport>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 256 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false
        }).RemoveAllLoggers();
        return services;
    }
}
