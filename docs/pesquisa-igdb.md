# Serviços de pesquisa IGDB — Etapa 3.3

Os serviços C# de pesquisa e consulta por ID estão integrados à página MVC de pesquisa e ao cadastro da biblioteca.
O catálogo externo fornece a identidade e os metadados; avaliações, status, observações e plataformas são pessoais.
O cadastro manual continua disponível mesmo sem Twitch/IGDB.
A infraestrutura de [autenticação da Etapa 3.2](autenticacao-igdb.md) é reutilizada sem alterações.

## Operações e contrato

Os consumidores backend utilizam `IIgdbClient`:

- `SearchAsync(string term, CancellationToken)`: consulta até 30 candidatos e retorna os dez melhores após classificação local.
  A correspondência textual tem maior peso que o tipo; jogos principais e remakes recebem preferência adicional.
  Somente empates preservam a ordem da API. IDs distintos não são mesclados.
- `GetByIdAsync(long igdbId, CancellationToken)`: retorna um jogo tipado ou null quando não encontrado.

Nenhuma operação aceita filtros livres, linguagem de consulta ou URLs externas.
Os modelos não têm credenciais, tokens, avaliação pessoal, status pessoal ou entidades EF.

`IgdbGameResult` é um contrato imutável, separado do DTO externo interno `IgdbGameResponse`:

| Campo | Significado |
|---|---|
| IgdbId | Identidade externa positiva |
| Title | Nome informado pela IGDB |
| CoverImageId | Identificador opcional da capa |
| FirstReleaseDate / ReleaseYear | Primeira data informada e seu ano, opcionais |
| GameTypeId / GameType | ID e descrição externa do tipo, quando fornecidos |
| EditionTitle | Título opcional da versão/edição |
| VersionParentIgdbId | Referência externa opcional da versão principal |

São consultados somente `id`, `name`, `cover.image_id`, `first_release_date`, `game_type.id`,
`game_type.type`, `version_title` e `version_parent`. Não utilizamos o campo obsoleto `category`.
O lançamento Unix é convertido para uma data UTC; não representa compra, região ou plataforma específica.

Não inferimos relações de remake a partir do nome ou de version_parent. Tipos externos desconhecidos são preservados
sem conversão para enum fixo. IDs diferentes continuam distintos mesmo com títulos iguais.
Repetições do mesmo ID são removidas preservando a primeira ocorrência; edições não são filtradas automaticamente.

Exemplo exclusivamente fictício de contrato interno:

```json
{
  "igdbId": 900001,
  "title": "Aventura das Estrelas — Gold",
  "coverImageId": "co_ficticia",
  "firstReleaseDate": "2021-06-10",
  "releaseYear": 2021,
  "gameTypeId": null,
  "gameType": null,
  "editionTitle": "Gold",
  "versionParentIgdbId": 900000
}
```

## Consulta HTTP e entrada segura

O cliente tipado é registrado por IHttpClientFactory em `AddIgdbSearch`, logo após o registro de autenticação.
Usa POST exclusivamente em `https://api.igdb.com/v4/games`, com Client-ID e Authorization: Bearer.
O token é solicitado ao IIgdbTokenProvider existente. Não há duplicação da autenticação.
Redirecionamentos, cookies e loggers do cliente HTTP estão desabilitados.

O termo é normalizado para Unicode Form C, aparado e tem espaços consecutivos reduzidos a um.
Exigimos 3 a 100 unidades UTF-16 normalizadas e limitamos também a entrada bruta a 1.024.
Controles, formatação invisível e separadores de linha/parágrafo são rejeitados, sem ecoar o termo no erro.
Aspas e barras invertidas são escapadas dentro de um único literal; ponto e vírgula permanece parte desse literal.
Campos, limite e filtros estruturais são definidos exclusivamente pelo código. IDs usam long positivo e formatação invariante.

Respostas bem-sucedidas têm limite de 256 KiB, verificado tanto no Content-Length quanto na leitura do stream,
e profundidade JSON máxima de 16. O parser rejeita respostas com mais de 100 linhas externas;
a pesquisa considera até 30 candidatos válidos e entrega no máximo dez após classificação.
IDs e títulos são obrigatórios; nomes têm limite de 300 caracteres, edição de 200 e tipo de 100.
Datas impossíveis, referências negativas e capas malformadas resultam em erro controlado.
Campos opcionais ausentes não impedem retornar um jogo; strings opcionais vazias são tratadas como ausentes.
Razor codifica os textos externos; o JavaScript usa textContent, sem interpretar HTML nos títulos ou metadados.

## Capas

`IgdbCoverImages.GetUrl` constrói somente URLs HTTPS no host fixo images.igdb.com, usando image_id validado.
Os tamanhos permitidos são `IgdbCoverSize.Small` (cover_small) e Big (cover_big).
Não aceita uma URL do usuário nem baixa imagens durante as consultas. ID ausente ou inválido produz null,
permitindo uma imagem substituta na pesquisa atual. Nenhuma imagem binária é gravada no SQLite.

## Cache e limites de chamadas

`IgdbQueryCache` é singleton com MemoryCache dedicado, até 100 entradas e validade fixa de cinco minutos.
As chaves têm prefixos separados para pesquisa e ID. Termos usam os mesmos espaços normalizados e caixa invariante,
preservando acentos; pesquisas equivalentes reutilizam a mesma lista somente leitura.
Resultados vazios e ausência por ID são respostas válidas e podem ser armazenados. Exceções e erros não são cacheados.
São armazenados apenas contratos tipados, nunca tokens ou credenciais. Reiniciar o processo limpa o cache.

A IGDB documenta quatro requisições por segundo e até oito abertas. O MVP utiliza `IgdbRequestScheduler`
singleton, uma chamada ativa por vez e espaçamento mínimo de 350 ms entre inícios, medido com relógio monotônico.
Pesquisas, consultas por ID e tentativas adicionais compartilham a mesma fila e os mesmos limites.
O timeout também permite cancelar a espera na fila. Isso é deliberadamente conservador para um único usuário local.
O cache é conferido novamente dentro da fila para evitar consultas duplicadas que já tenham sido concluídas.

Não há rate limiting distribuído: instâncias distintas não compartilham esses contadores. A interface
aplica debounce de 400 ms e cancela pesquisas anteriores; o endpoint local aceita 30 consultas por minuto.

## Falhas e tentativas limitadas

`IgdbQueryException` fornece Failure, StatusCode opcional e RetryAfter opcional, com mensagem estática e sem
corpo bruto ou InnerException sensível. Diferencia entrada inválida, integração desabilitada/não configurada,
configuração inválida, falha de autenticação, não autorizado, limite, erro HTTP, timeout, indisponibilidade e resposta inválida.
Uma lista vazia não significa indisponibilidade: falhas são exceções controladas, não listas vazias.

- 401: invalida condicionalmente o token rejeitado, obtém outro pelo provedor e repete uma única vez.
  Um segundo 401 encerra a operação, também invalidando o token rejeitado.
- 429: interpreta Retry-After como segundos ou data HTTP. Sem cabeçalho usa um segundo.
  No máximo uma repetição, com espera automática de até cinco segundos.
  Esperas maiores geram falha imediata e mantêm o prazo compartilhado de bloqueio: novas chamadas não ignoram o Retry-After.
  Não truncamos uma espera longa para reenviar antes do prazo da API.
- Quando 401 e 429 aparecem na mesma operação, o máximo é três requisições de jogos: inicial e uma tentativa para cada caso.
- Outros erros HTTP, incluindo 5xx, não têm repetição automática. Não lemos seus corpos para mensagens de erro.
- `Igdb:TimeoutSeconds`, já existente, limita cada tentativa/fila; chamadas de token seguem seu próprio timeout.
  Cancelamento do consumidor é propagado como OperationCanceledException; timeout é uma falha tipada distinta.

Não há chamada de rede na inicialização ou na resolução dos serviços. A biblioteca pode funcionar sem credenciais.
Os valores reais continuam no User Secrets existente; não foram lidos ou modificados para desenvolver esta etapa.

## Integração com a biblioteca

SearchAsync consulta até 30 candidatos em uma chamada e retorna os dez melhores após classificação local.
Os resultados classificados são armazenados no cache existente. GetByIdAsync mantém a consulta por identidade.
Os critérios e limites estão documentados em [relevancia-igdb.md](relevancia-igdb.md).

A interface MVC usa SearchAsync para apresentar resultados e GetByIdAsync para resolver a seleção.
O cadastro resolve primeiro Game por IgdbId local e não associa títulos manuais automaticamente.
Metadados do catálogo ficam separados das posses pessoais. A migration aditiva e a persistência da etapa 3.5
estão descritas em [cadastro-igdb.md](cadastro-igdb.md); faça backup antes do F5, que aplica migrations em Development.

## Testes e validação

Historicamente, a etapa 3.3 preservou os 145 testes anteriores. Seus novos testes usam HttpMessageHandler simulado,
tempo virtual para cache/espaçamento/Retry-After e SQLite em memória para provar ausência de escrita pela pesquisa.
Incluem o IgdbTokenProvider real ligado a um handler de autenticação simulado, sem usar credenciais reais.
Também cobrem Unicode, escapamento, resultados opcionais, IDs, versões distintas, limites de resposta,
cache compartilhado limitado, concorrência, 401, 429, erros HTTP/JSON, timeout, cancelamento e segurança das mensagens.

```powershell
dotnet restore NextGameAI.slnx
dotnet build NextGameAI.slnx --no-restore
dotnet test NextGameAI.slnx --no-build
git diff --check
```

A validação histórica da etapa 3.3 aprovou 210 testes, com build sem avisos ou erros e sem chamadas reais nessa execução.
As etapas seguintes validaram autenticação, pesquisa real, interface e cadastro integrado.
A validação final após a auditoria aprovou 301 testes (289 anteriores e 12 regressões de acesso SQLite),
com build sem avisos ou erros. Nesta correção não foram feitas chamadas reais nem acessado o banco pessoal.

Documentação oficial consultada: [IGDB e campos](https://api-docs.igdb.com/#game),
[limites](https://api-docs.igdb.com/#rate-limits), [pesquisa](https://api-docs.igdb.com/#search)
e [imagens](https://api-docs.igdb.com/#images).
