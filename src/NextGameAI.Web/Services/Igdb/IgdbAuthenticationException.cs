using System.Net;

namespace NextGameAI.Web.Services.Igdb;

public enum IgdbAuthenticationFailure
{
    Disabled, NotConfigured, InvalidConfiguration, CredentialsRejected,
    HttpError, Timeout, Unavailable, InvalidResponse
}

public sealed class IgdbAuthenticationException : Exception
{
    public IgdbAuthenticationFailure Failure { get; }
    public HttpStatusCode? StatusCode { get; }

    // No response body, credential values or inner HTTP/JSON exceptions are retained.
    public IgdbAuthenticationException(IgdbAuthenticationFailure failure, HttpStatusCode? statusCode = null)
        : base(failure switch
        {
            IgdbAuthenticationFailure.Disabled => "A integração IGDB está desabilitada.",
            IgdbAuthenticationFailure.NotConfigured => "As credenciais da integração IGDB não estão configuradas.",
            IgdbAuthenticationFailure.InvalidConfiguration => "O timeout da integração IGDB deve estar entre 1 e 60 segundos.",
            IgdbAuthenticationFailure.CredentialsRejected => "A Twitch recusou a autenticação da aplicação.",
            IgdbAuthenticationFailure.HttpError => "A Twitch não conseguiu concluir a autenticação.",
            IgdbAuthenticationFailure.Timeout => "A autenticação da Twitch excedeu o tempo limite.",
            IgdbAuthenticationFailure.Unavailable => "Não foi possível conectar à autenticação da Twitch.",
            _ => "A Twitch retornou uma resposta de autenticação inválida."
        })
    {
        Failure = failure;
        StatusCode = statusCode;
    }
}
