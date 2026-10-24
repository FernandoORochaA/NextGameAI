using System.Net;

namespace NextGameAI.Web.Services.Igdb;

public enum IgdbQueryFailure
{
    InvalidInput, Disabled, NotConfigured, InvalidConfiguration, AuthenticationUnavailable,
    Unauthorized, RateLimited, HttpError, Timeout, Unavailable, InvalidResponse
}

public sealed class IgdbQueryException : Exception
{
    public IgdbQueryFailure Failure { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public IgdbQueryException(IgdbQueryFailure failure, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
        : base(failure switch
        {
            IgdbQueryFailure.InvalidInput => "Informe um título de 3 a 100 caracteres ou um identificador positivo, sem caracteres de controle.",
            IgdbQueryFailure.Disabled => "A integração IGDB está desabilitada.",
            IgdbQueryFailure.NotConfigured => "A integração IGDB não está configurada.",
            IgdbQueryFailure.InvalidConfiguration => "A configuração da integração IGDB é inválida.",
            IgdbQueryFailure.AuthenticationUnavailable => "Não foi possível autenticar a consulta à IGDB.",
            IgdbQueryFailure.Unauthorized => "A IGDB não autorizou a consulta.",
            IgdbQueryFailure.RateLimited => "A IGDB recebeu muitas consultas. Aguarde antes de tentar novamente.",
            IgdbQueryFailure.Timeout => "A consulta à IGDB excedeu o tempo limite.",
            IgdbQueryFailure.InvalidResponse => "A IGDB retornou uma resposta inválida.",
            _ => "A IGDB está temporariamente indisponível."
        })
    {
        Failure = failure; StatusCode = statusCode; RetryAfter = retryAfter;
    }
}
