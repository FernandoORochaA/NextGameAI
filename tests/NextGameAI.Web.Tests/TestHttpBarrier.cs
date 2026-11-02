using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace NextGameAI.Web.Tests;

internal sealed record SimulatedTestHttp(string Name, Func<HttpMessageHandler> Create);

internal static class SimulatedTestHttpRegistration
{
    internal static IServiceCollection AddSimulatedHttp(this IServiceCollection services, string name,
        Func<HttpMessageHandler> create)
    {
        services.AddSingleton(new SimulatedTestHttp(name, create));
        return services;
    }
}

internal sealed class TestHttpBarrier(IEnumerable<SimulatedTestHttp> simulations) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        var simulation = simulations.SingleOrDefault(x => x.Name == builder.Name);
        var handler = simulation?.Create() ?? new BlockedTestHttpHandler();
        // Only terminal fake handlers are accepted: a delegated socket transport must never be allowed.
        if (handler is SocketsHttpHandler or HttpClientHandler or DelegatingHandler)
            throw new InvalidOperationException("O handler de testes deve ser simulado e não pode delegar à rede.");
        builder.PrimaryHandler = handler;
        builder.AdditionalHandlers.Clear();
    };
}

internal sealed class BlockedTestHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("HTTP de saída bloqueado: registre um handler simulado no teste.");
}
