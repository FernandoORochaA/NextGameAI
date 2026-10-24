# Etapa 4A — Persistência de metadados do catálogo

Implementação restrita à modelagem, configuração EF Core, migration e testes isolados.
Etapas 4B (consulta/enriquecimento) e 4C (interface) não foram implementadas.
O ROADMAP oficial não foi alterado e a Etapa 4 não é declarada concluída.

## Atenção antes de executar a aplicação

Na implementação inicial, a migration foi gerada e testada somente com SQLite isolado,
sem acesso ao banco pessoal. A aplicação posterior ao banco pessoal foi autorizada e realizada
manualmente no ambiente local em 24/10/2026.
Program.cs permanece inalterado e aplica migrations pendentes automaticamente em Development.
Novas migrations continuam exigindo aprovação explícita antes de executar F5 ou iniciar o servidor.
Não foram realizadas chamadas reais à IGDB durante a implementação 4A.

A auditoria pós-migration foi aprovada: as seis tabelas novas estão presentes, o SQLite está íntegro,
sem violações de chaves estrangeiras, e os registros pessoais restantes foram preservados,
considerando as operações posteriores declaradas pelo usuário. Os backups pré-4A e pós-4A foram criados e validados.

## Estruturas e identidade

As seis tabelas novas referenciam Game, sem alterar sua identidade ou qualquer coluna das tabelas antigas:

| Entidade/tabela | Finalidade e chave |
|---|---|
| CatalogTaxonomies | Referência compartilhada de gênero, tema, modo, perspectiva ou keyword; PK local Id |
| GameTaxonomies | Associação explícita, PK composta GameId/CatalogTaxonomyId |
| GameTimeToBeats | Duração opcional, no máximo uma linha por GameId |
| CatalogPlatforms | Plataforma externa da IGDB, PK local Id, IgdbId único |
| GameMultiplayerModes | Detalhes opcionais por jogo e plataforma externa |
| GameEnrichmentStates | Controle de consulta por GameId e Section |

CatalogTaxonomy.Kind distingue Genre, Theme, GameMode, PlayerPerspective e Keyword.
O índice único (Kind, IgdbId) permite que tipos diferentes usem o mesmo número externo.
Nome não é identidade nem precisa ser único; nenhuma tradução foi implementada.
Um jogo pode ter várias referências e vários jogos podem compartilhar a mesma referência.
Uma tabela discriminada foi escolhida porque os cinco tipos possuem a mesma estrutura e validações;
isso evita cinco pares de classes/tabelas repetidos sem introduzir uma arquitetura genérica de serviços.
O join garante unicidade por jogo/referência. A Etapa 4B deverá respeitar Kind ao importar cada relação.

Game, LibraryEntry, GameOwnership, Store e Platform não foram modificados.
CatalogPlatform não representa posse e não se conecta às plataformas pessoais.
Jogos manuais permanecem válidos sem metadados. Não há associação pelo título nem cópia entre versões.
As relações existentes de perfil, status, notas, observações e posses permanecem intactas.

## Duração e proveniência

GameTimeToBeat possui IgdbId opcional do registro externo de duração, Source (padrão IGDB),
HastilySeconds, NormallySeconds e CompletelySeconds como long opcionais, e SubmissionCount como int opcional.
As durações permanecem em segundos; nenhum valor é inferido e não se armazenam conversões em horas.
Ausência de linha significa que não há estimativa persistida; campos nulos continuam desconhecidos.
A distinção entre ainda não consultado e consulta sem dados está no controle de atualização.
Os valores são estimativas de conclusão, não garantias nem duração de sessão.

## Multiplayer detalhado

GameMultiplayerMode possui identidade local, IgdbId opcional do registro externo, GameId,
CatalogPlatformId opcional e Source. Campos correspondem ao contrato investigado:

| Propriedade | Campo IGDB |
|---|---|
| CampaignCoop | campaigncoop |
| DropIn | dropin |
| LanCoop | lancoop |
| OfflineCoop / OfflineCoopMax | offlinecoop / offlinecoopmax |
| OfflineMax | offlinemax |
| OnlineCoop / OnlineCoopMax | onlinecoop / onlinecoopmax |
| OnlineMax | onlinemax |
| SplitScreen / SplitScreenOnline | splitscreen / splitscreenonline |

Todos os indicadores são bool? e todos os limites int?. Ausência não vira false ou zero.
O índice único GameId/CatalogPlatformId impede repetição na mesma plataforma.
Um índice parcial único em GameId, quando CatalogPlatformId é nulo, permite no máximo um registro de
plataforma desconhecida por jogo. É uma escolha conservadora contra duplicações:
se a fonte trouxer vários registros sem plataforma, a Etapa 4B deverá resolver a ambiguidade antes de gravar,
sem mesclar silenciosamente registros conflitantes. IgdbId de multiplayer, quando informado, também é único.
Não há campo ou inferência de crossplay.

## Controle de atualização preparado

Cada GameEnrichmentState registra uma seção: Genres, Themes, GameModes, PlayerPerspectives,
Keywords, TimeToBeat ou Multiplayer. O controle por seção evita representar sucesso total
quando uma consulta complementar falhar.

- Sem linha da seção: nunca consultada.
- LastAttemptAtUtc: última tentativa, obrigatório.
- LastResult: Success, TemporaryFailure ou NotFound.
- LastSuccessAtUtc: última atualização bem-sucedida, opcional.
- Source: proveniência, padrão IGDB.
- Success com relações vazias/campos ausentes: consulta concluída sem esses atributos.
- Falha posterior pode manter LastSuccessAtUtc antigo e todos os dados anteriores.
- NotFound representa ausência do registro externo, distinta de falha temporária.

CHECKs exigem que o sucesso tenha timestamp correspondente à tentativa e que o último sucesso
não seja posterior à tentativa. Os timestamps devem ser fornecidos em UTC pela futura integração.
A persistência não inicia consultas, não apaga dados nem executa atualizações automaticamente.

A política planejada considera dados elegíveis para atualização sob demanda após 30 dias do último sucesso.
O prazo e o estado de obsolescência não são persistidos: serão calculados na Etapa 4B.
Falhas não devem apagar o conteúdo antigo. Índices e PKs impedem duplicação; upsert, transações
de enriquecimento, seleção conservadora e semântica de limpeza após sucesso serão implementados somente em 4B,
após revisão. Não há tarefas agendadas.

## Integridade e spoilers

Identificadores externos são positivos; valores de duração, contagem e limites não podem ser negativos.
Enums, nomes/proveniência não vazios e coerência temporal têm restrições no banco.
DataAnnotations também participam da validação existente de SaveChanges.
Os comprimentos são validados pelo modelo; SQLite não impõe automaticamente limites VARCHAR.
Índices únicos de identidades opcionais usam filtros IS NOT NULL.

Todas as FKs novas usam Restrict. Não se apagam automaticamente jogos, referências compartilhadas,
metadados ou dados pessoais por cascata nova. Remover referências exige resolver associações explicitamente.
A exclusão já existente de LibraryEntry continua preservando Game e seu catálogo.

Keywords são persistidas sem qualquer afirmação de segurança para exibição. Não existe flag que as declare
seguras por padrão. A Etapa 4B deverá selecionar conservadoramente o que usar; a Etapa 4C precisará decidir
o que pode ser apresentado. Não há filtro simplista de spoilers, tradução, summary, storyline,
descrições completas, pontuação de keywords, recomendação ou IA.

## Design-time e migration

NextGameAiDesignTimeFactory implementa IDesignTimeDbContextFactory e constrói somente opções SQLite:
Data Source=:memory:;Foreign Keys=True. Não lê configuração, credenciais ou arquivos do banco pessoal.
A geração EF confirmou o uso dessa fábrica, sem construir o host MVC ou executar Program.cs.
O startup não foi alterado. Não executar comandos database update contra uma conexão pessoal.

Migration: 20261024220151_AddCatalogEnrichmentMetadata.
Up contém somente CreateTable e CreateIndex para as seis tabelas novas.
Não há rebuild, DropTable, DropColumn ou alterações em Game/LibraryEntry/GameOwnership.
Down remove somente as seis tabelas novas, na ordem das dependências; perde os metadados da 4A,
mas não os registros antigos. Downgrade foi testado exclusivamente em memória.
As migrations anteriores foram preservadas e o snapshot corresponde ao modelo atual.

## Validação isolada

Última execução documentada: compilação com zero avisos e erros; suíte xUnit com 323 aprovados, zero falhas/ignorados;
301 casos anteriores preservados e 22 novos casos de persistência/migration.
Nenhum pacote foi instalado ou adicionado.

Os testes novos constroem SQLite em memória com Foreign Keys=True e aplicam migrations, sem EnsureCreated
nem cópia do banco pessoal. Um cenário parte de IgdbCatalogMetadata, insere original, remake e jogo manual,
bibliotecas com notas zero/meia estrela/ausente, status, observações, abandono e múltiplas posses.
Compara todas as linhas das seis tabelas antigas antes/depois de Up e Down e executa foreign_key_check.
Verifica também os tipos de operações da migration, restrições, nulidade e preservação de referências.

Os testes MVC anteriores usam somente TestServer/WebApplicationFactory em Testing e substituem
a conexão por SQLite em memória; nenhum servidor real, F5 ou host com conexão pessoal foi iniciado.
Os serviços externos continuam simulados durante a suíte.

## Próxima decisão

Revisar esta modelagem e seus trade-offs antes de autorizar 4B.
A migration 4A já foi aplicada e validada no ambiente local. As etapas 4B e 4C permanecem pendentes
e exigem autorização própria; a Etapa 4 inteira não está concluída.
Nenhum endpoint, controller, cliente IGDB ou componente visual foi alterado nesta etapa.
