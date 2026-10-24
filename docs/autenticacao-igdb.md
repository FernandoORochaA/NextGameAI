# Autenticação Twitch/IGDB — Etapa 3.2

Este documento descreve a autenticação criada na etapa 3.2 e reutilizada pelo cliente IGDB atual.
A pesquisa visual e o cadastro integrado foram implementados nas etapas seguintes.
Não há endpoints de token, autenticação de usuários ou chamadas externas na inicialização.
A biblioteca local continua independente da Twitch.

## Configuração local

O projeto web já possui um UserSecretsId configurado pelo Visual Studio. Preserve esse identificador;
não inicialize outro e não coloque credenciais em arquivos versionados.

No Visual Studio 2026, use **Gerenciar Segredos do Usuário** no projeto NextGameAI.Web.
As chaves necessárias são `Igdb:ClientId` e `Igdb:ClientSecret`. Não é necessário salvar um token:
ele será obtido pelo serviço quando um consumidor backend o solicitar.
Nunca copie segredos para appsettings, launchSettings, testes, logs, HTML ou relatórios.
O Secret Manager mantém os valores fora do repositório, mas não os criptografa.

`WebApplication.CreateBuilder` carrega User Secrets em Development quando o projeto possui UserSecretsId.
O registro usa IConfiguration e Options Pattern, sem ler diretamente secrets.json.

| Configuração | Padrão | Comportamento |
|---|---|---|
| Igdb:Enabled | true | false desabilita a obtenção de tokens |
| Igdb:ClientId | ausente | Deve vir da configuração local de segredos |
| Igdb:ClientSecret | ausente | Deve vir da configuração local de segredos |
| Igdb:TimeoutSeconds | 10 | Inteiro de 1 a 60 segundos |

A criação do host não exige credenciais e não faz autenticação. `IsConfigured` indica somente se
a configuração está habilitada e preenchida com um timeout válido; não comprova conectividade ou credenciais válidas.
Sem configuração, GetTokenAsync falha de maneira controlada e a biblioteca permanece utilizável.
As opções são uma configuração por processo: reinicie a aplicação depois de alterar credenciais ou habilitação.

## Componentes e ciclo de vida

- `IgdbOptions`: configuração tipada.
- `IIgdbTokenProvider`: IsConfigured, GetTokenAsync e Invalidate(rejectedToken).
- `IgdbTokenProvider`: singleton com cache em memória e SemaphoreSlim para obtenção assíncrona exclusiva.
- `IgdbAuthenticationException`: motivo estruturado e mensagens sem valores sensíveis.
- `AddIgdbAuthentication`: registro das opções, TimeProvider, cliente HTTP nomeado e singleton.

O singleton recebe IHttpClientFactory, não um cliente tipado de curta duração.
Cada autenticação cria e descarta um HttpClient pelo factory, reutilizando o gerenciamento de handlers do .NET.
TimeProvider permite testar a passagem do tempo sem esperar pela expiração real.
Nenhum pacote novo é necessário.

## Obtenção e reutilização

Quando solicitado, o serviço faz POST em `https://id.twitch.tv/oauth2/token`, usando
application/x-www-form-urlencoded com client_id, client_secret e grant_type=client_credentials no corpo.
O endpoint é fixo, sem parâmetros na URL, sem cookies e sem seguir redirecionamentos.
Os loggers do cliente HTTP nomeado estão desabilitados. A resposta fica limitada a 64 KiB.

São validados access_token, token_type=bearer e expires_in positivo. Respostas sem esses campos,
com tipos incorretos, token malformado ou duração impossível são rejeitadas sem preservar o corpo.
O token fica apenas na memória do processo: não há armazenamento em arquivos, SQLite ou navegador.

A validade parte do instante anterior à requisição, para não acrescentar o tempo de rede à duração recebida.
A margem de segurança é 10% da duração, limitada a 60 segundos. Uma resposta que já consumiu
essa janela de reutilização é rejeitada. Chamadas concorrentes conferem o cache novamente depois de obter o semáforo;
assim, uma obtenção bem-sucedida atende os demais chamadores sem autenticações duplicadas.

App access tokens não são renovados por refresh_token. Depois da expiração ou invalidação,
o próximo GetTokenAsync solicita outro token com Client Credentials. Não há renovação em segundo plano.

## Falhas e cancelamento

As falhas usam IgdbAuthenticationFailure: Disabled, NotConfigured, InvalidConfiguration,
CredentialsRejected, HttpError, Timeout, Unavailable e InvalidResponse.
HTTP 400/401 é tratado como rejeição da autenticação; demais códigos não bem-sucedidos usam HttpError.
O código HTTP fica disponível em StatusCode, sem corpo bruto, segredo ou token na exceção.
Erros de transporte e JSON são substituídos por mensagens controladas, sem InnerException sensível.
Cancelamento solicitado pelo consumidor é propagado como OperationCanceledException;
timeout da chamada é identificado separadamente. Falhas não são armazenadas como tokens válidos.

Não existe retry automático do endpoint de autenticação. Uma nova chamada pode tentar novamente.
O cliente IGDB atual limita tentativas e trata indisponibilidade sem prejudicar dados locais.

## Uso no cliente IGDB atual

O cliente backend solicita GetTokenAsync(cancellationToken) e usa o resultado apenas
no cabeçalho Authorization: Bearer, junto do Client-ID da configuração. Nunca retorne o token ao navegador.
Se a IGDB responder 401, chame Invalidate com o token efetivamente rejeitado e obtenha outro;
repita a consulta apenas uma vez. A invalidação condicional impede que um 401 atrasado de um token antigo
apague um token mais recente obtido por outra requisição.

O cliente IGDB, a pesquisa e o tratamento de seus 401 foram implementados após a etapa 3.2.
Não há validação periódica em /oauth2/validate. A configuração válida por si só não faz chamadas.

## Testes e validação real posterior

Os testes utilizam HttpMessageHandler simulado e valores sintéticos. Não carregam User Secrets,
não autenticam na Twitch e não usam o banco pessoal. Cobrem formulário HTTP, cache, expiração,
concorrência, invalidação, configurações ausentes, falhas HTTP/JSON, timeout, cancelamento e mensagens seguras.
Os testes de biblioteca usam o ambiente Testing e SQLite em memória.

Execute na raiz:

```powershell
dotnet restore NextGameAI.slnx
dotnet build NextGameAI.slnx --no-restore
dotnet test NextGameAI.slnx --no-build
git diff --check
```

A validação real ocorreu em etapas separadas e autorizadas. Não há endpoint público de diagnóstico ou token.
O resultado de testes simulados não deve ser apresentado como comprovação de autenticação real.
A validação final após a auditoria aprovou 301 testes, sem alterar autenticação ou credenciais.

Referências oficiais: [Client Credentials](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#client-credentials-grant-flow),
[renovação de tokens](https://dev.twitch.tv/docs/authentication/refresh-tokens/),
[IGDB](https://api-docs.igdb.com/#authentication) e
[User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0).
