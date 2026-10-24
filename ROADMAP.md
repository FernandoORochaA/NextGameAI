# NextGame AI — Roadmap técnico e funcional

> Roadmap oficial do NextGame AI.
> Documento vivo, atualizado conforme a evolução do projeto.
> Este documento registra a evolução do produto. Não autoriza implementar as etapas futuras.

## Visão do produto

Ajudar o jogador a escolher seu próximo jogo conforme sua vontade atual, biblioteca, experiências anteriores
e contexto. A experiência futura deverá compreender linguagem natural, considerar tempo disponível quando
informado e explicar recomendações sem spoilers, tanto de jogos possuídos quanto de jogos ainda não adquiridos.

O MVP é local, com um único perfil e execução pelo F5 no Visual Studio 2026.
Funcionalidade e validação no navegador precedem o visual definitivo.
Não há prazos definidos neste roadmap.

## Estado verificado e convenções

Base consultada: commit `c56c53f` — integração IGDB ao catálogo e biblioteca.
O README apresenta a visão; código, migrations, testes e documentação em docs sustentam o estado abaixo.

- Stack atual: C#, .NET 10, ASP.NET Core MVC, EF Core 10, SQLite, Twitch OAuth2, IGDB, HTML/CSS/JavaScript e xUnit.
- Solução: [NextGameAI.slnx](NextGameAI.slnx), com aplicação em [src/NextGameAI.Web](src/NextGameAI.Web)
  e testes em [tests/NextGameAI.Web.Tests](tests/NextGameAI.Web.Tests).
- Biblioteca e pesquisa integradas; sem autenticação própria de usuários, recomendações, conversas ou IA.
- Última validação histórica documentada da Etapa 3: **301 testes aprovados**, build sem avisos ou erros.
  Não foi executada novamente para elaborar este documento; esse número não garante execuções futuras.
- O desenvolvedor informou validação manual pelo navegador e aplicação da migration de metadados ao banco pessoal.
  Este planejamento não acessa o banco e não certifica seu estado interno.

Status:

| Status | Significado |
|---|---|
| [CONCLUÍDA] | Entregas confirmadas por código e validações documentadas |
| [EM ANDAMENTO] | Implementação iniciada e ainda não aceita |
| [PLANEJADA] | Objetivo proposto, ainda sem implementação; desenho requer aprovação |
| [A DEFINIR] | Escopo ou necessidade dependem de decisão futura |

Nenhuma etapa de implementação está marcada como em andamento nesta revisão.

## Sequência e prioridades

| Etapa | Nome | Status | Prioridade e dependências |
|---|---|---|---|
| 1 | Estrutura inicial | [CONCLUÍDA] | Fundação |
| 2 | Biblioteca pessoal | [CONCLUÍDA] | Depende de 1 |
| 3 | Twitch/IGDB e cadastro integrado | [CONCLUÍDA] | Depende de 2 |
| 4 | Enriquecimento do catálogo | [PLANEJADA] | Próxima prioridade; depende de 3 |
| 5 | Seleção de candidatos para recomendação | [PLANEJADA] | Depende de 4 e dos dados pessoais de 2 |
| 6 | Recomendações por linguagem natural | [PLANEJADA] | Depende de 5 e da avaliação de provedor |
| 7 | Histórico, conversas e personalização progressiva | [PLANEJADA] | Depende do primeiro fluxo de 6 |
| 8 | Evolução da experiência e visual | [PLANEJADA] | Evolução ampla após 6; ajustes de uso podem ocorrer antes |
| 9 | Contas e bibliotecas individuais | [A DEFINIR] | Expansão além do MVP; depende de necessidade multiusuário |
| 10 | Publicação e operação | [A DEFINIR] | Depende do escopo de publicação; requer 9 se houver usuários distintos |

A sequência é revisável. Segurança, acessibilidade básica, testes e preservação de dados são transversais.
As etapas 9 e 10 não bloqueiam o MVP local de recomendação.

## Etapa 1 — Estrutura inicial [CONCLUÍDA]

**Objetivo e benefício:** disponibilizar uma aplicação executável no navegador para evoluir o produto.

**Entregas comprovadas:**

- Solução .slnx e projeto ASP.NET Core MVC net10.0, nullable e configuração por ambiente.
- Controllers, Views Razor, layout, arquivos estáticos e página inicial.
- Perfis de execução HTTP/HTTPS e abertura no navegador pelo Visual Studio.
- Regras Git para arquivos locais, compilação, SQLite, segredos e chaves privadas.
- A infraestrutura atual de testes está em projeto xUnit separado; não se atribui ao primeiro commit
  a criação de todos os testes que foram acrescentados depois.

**Dependências:** SDK e ferramentas compatíveis com o projeto.

**Critérios cumpridos:** estrutura MVC executável e compilável, confirmada também nas validações das etapas seguintes.

**Evidências:** commit `a4a9323`, [Program.cs](src/NextGameAI.Web/Program.cs),
[HomeController.cs](src/NextGameAI.Web/Controllers/HomeController.cs),
[launchSettings.json](src/NextGameAI.Web/Properties/launchSettings.json) e [.gitignore](.gitignore).

**Riscos e decisões:** o ambiente de cada máquina precisa ser validado; execução local não equivale a implantação pública.

## Etapa 2 — Biblioteca pessoal [CONCLUÍDA]

**Objetivo e benefício:** organizar os jogos possuídos e registrar a experiência pessoal.

A biblioteca pessoal é destinada somente a jogos realmente possuídos pelo jogador. Game identifica o catálogo;
LibraryEntry e suas posses representam a inclusão explícita na biblioteca e os dados pessoais.
Um jogo existir no catálogo ou ser recomendado não significa que pertence à biblioteca.

**Entregas comprovadas:**

- Cadastro manual, listagem, detalhes, edição e exclusão da entrada pessoal.
- Status Não iniciado, Jogando, Finalizado e Abandonado; motivo opcional de abandono.
- Avaliação opcional de zero a cinco estrelas em passos de meia estrela.
  Armazenamento de 0 a 10; null significa não avaliado e é distinto de zero.
- Observações pessoais e seleção múltipla de PC, PlayStation, Nintendo, Xbox e Outros.
- Agrupamento de plataformas antigas, preservação das posses detalhadas mantidas e confirmação para remoção.
- Catálogo Game separado de LibraryEntry e GameOwnership, com UserProfile local.
- SQLite, índices únicos, chaves estrangeiras, validações de servidor, antiforgery e transações com rollback.
- Exclusão da entrada não apaga o catálogo compartilhado; edição pessoal não substitui a identidade do jogo.

**Dependências:** Etapa 1.

**Critérios cumpridos:** fluxos de CRUD, validações e preservação de dados cobertos por testes isolados;
formulário simplificado e persistência validados conforme documentação e relato do desenvolvedor.

**Evidências:** commit `4d7c65b`, [LibraryService.cs](src/NextGameAI.Web/Services/LibraryService.cs),
[LibraryModels.cs](src/NextGameAI.Web/Models/LibraryModels.cs), migration InitialLibrary,
[biblioteca](docs/biblioteca.md) e [formulário simplificado](docs/formulario-simplificado.md).

**Riscos e decisões:** um único perfil é resolvido pelo servidor, sem login. As referências Store e Platform continuam
existindo internamente; os grupos pessoais não comprovam plataformas externas de lançamento nem uma loja comercial.

## Etapa 3 — Integração Twitch/IGDB [CONCLUÍDA]

**Objetivo e benefício:** descobrir o jogo correto e cadastrá-lo sem confundir original, remake ou edição.

**Entregas comprovadas:**

- Client Credentials OAuth2 da Twitch no backend; configuração por Options e User Secrets em Development.
- Provedor de tokens com cache em memória, renovação após expiração e invalidação condicional.
- IIgdbClient com SearchAsync e GetByIdAsync, contratos tipados e campos opcionais.
- Tratamento de game_type.id zero para Main Game, IDs de jogo positivos e versões independentes por IgdbId.
- Pesquisa visual com debounce de 400 ms, cancelamento e descarte de respostas antigas.
- Capas por URL segura do CDN, título, ano, tipo, edição e identificador.
- Consulta de até 30 candidatos em uma chamada e classificação local para entregar os dez melhores.
  Correspondência textual tem maior peso que o bônus de tipo; Main Game e Remake recebem preferência adicional.
  Empates preservam a ordem externa; não há filtros que excluam tipos automaticamente.
- Cache tipado por cinco minutos, até 100 entradas, uma requisição externa ativa e espaçamento de 350 ms.
- Limite local de 30 consultas de pesquisa por minuto e tratamento limitado de HTTP 401/429.
- Fluxo Minha biblioteca → Adicionar jogo → pesquisa → Adicionar à biblioteca → formulário pessoal → detalhes.
  Alternativa “Não encontrou o jogo? Cadastrar manualmente”, disponível mesmo sem IGDB.
- Identidade resolvida no servidor: catálogo local primeiro, depois GetByIdAsync quando necessário.
  Metadados do HTML não são autoridade; jogos manuais não são associados automaticamente pelo nome.
- Reutilização de Game por IgdbId, prevenção de segunda entrada por perfil/Game e acesso amigável à entrada existente.
- Consulta externa fora da transação; nova verificação de unicidade dentro dela e rollback de falhas de gravação.
- Tratamento de falhas esperadas de acesso SQLite, preservando o formulário, sem ocultar erros inesperados.
- Migration aditiva IgdbCatalogMetadata e testes de migração com dados antigos em SQLite isolado.

**Metadados atuais:** Game persiste IgdbId, Title, EditionTitle, CoverImageId, FirstReleaseDate, GameTypeId e GameType.
O contrato externo também recebe VersionParentIgdbId, mas esse campo não é persistido em Game atualmente.
Não se armazena resposta bruta nem imagem binária.

**Dependências:** Etapa 2, configuração local de credenciais e disponibilidade externa para novas consultas.

**Critérios cumpridos:** autenticação e pesquisas reais controladas registradas, cadastro e navegação validados,
suíte histórica de 301 testes aprovada após auditoria e correções. Testes automatizados usam HTTP simulado
e SQLite isolado, sem exigir internet ou banco pessoal.

**Evidências:** commit `c56c53f`, [serviços IGDB](src/NextGameAI.Web/Services/Igdb),
[CatalogController.cs](src/NextGameAI.Web/Controllers/CatalogController.cs),
[autenticação](docs/autenticacao-igdb.md), [pesquisa](docs/pesquisa-igdb.md),
[interface](docs/pesquisa-visual-igdb.md), [cadastro](docs/cadastro-igdb.md) e [relevância](docs/relevancia-igdb.md).

**Limitações:** sem atualização automática dos metadados, paginação, enriquecimento amplo ou recomendações.
A classificação só ordena candidatos encontrados pela IGDB; não identifica preferências pessoais.
Migrations são aplicadas automaticamente no startup em Development; mudanças futuras exigem backup e teste prévio.

## Etapa 4 — Enriquecimento do catálogo [PLANEJADA]

**Objetivo e benefício:** obter dados suficientes para comparar experiências de jogo e fundamentar recomendações.

**Dependências:** Etapa 3. Primeira entrega recomendada: inventário e amostra de dados antes de mudar o esquema.

**Entregas técnicas propostas:**

1. Confirmar no contrato vigente da IGDB disponibilidade, qualidade e significado de gêneros, temas, palavras-chave,
   modos de jogo, plataformas de lançamento, datas e descrições. Esses campos ainda não são consultados pelo cliente atual.
2. Aprovar um conjunto mínimo: gêneros, temas e modos primeiro; palavras-chave e outras características somente
   se melhorarem cenários concretos. Não copiar o catálogo inteiro nem respostas brutas.
3. Separar fatos do catálogo de sinais interpretados: “história emocionante” ou “experiência leve” não são
   garantias derivadas apenas de um gênero. Registrar proveniência e incerteza quando houver inferência futura.
4. Propor referências e relações locais somente para os atributos aprovados, com identidade externa e nomes.
   Manter plataformas disponíveis separadas das plataformas que o jogador possui.
5. Definir atualização sob demanda por IgdbId, validade, instante de atualização e resultado da última tentativa.
   Evitar uma chamada por atributo, consultas ilimitadas, duplicações e sobrescrita de dados pessoais.
6. Tratar descrições como conteúdo externo não confiável; não apresentar storyline nem detalhes narrativos
   automaticamente. Preferir características estruturadas e resumos seguros, se necessários.
7. Validar eventual migration aditiva em SQLite isolado com dados antigos; garantir rollback e falhas parciais controladas.

**Critérios de conclusão:**

- [ ] Campos e política de atualização aprovados com exemplos reais, incluindo ausência de metadados.
- [ ] Dados mínimos persistidos e recuperados sem alterar LibraryEntry ou GameOwnership.
- [ ] Atualizações idempotentes, limitadas e tolerantes à indisponibilidade; dados pessoais preservados.
- [ ] Testes simulados, migration isolada se necessária e validação no navegador concluídos e documentados.
- [ ] Nenhuma descrição com spoiler exibida por padrão.

**Riscos e decisões:** taxonomia incompleta, ruído de palavras-chave, idiomas e desatualização.
Definir necessidade de resumo e relações entre versões. Duração estimada exige fonte própria verificada:
FirstReleaseDate não mede duração, e não há fonte de tempo de jogo integrada hoje.

## Etapa 5 — Seleção de candidatos para recomendação [PLANEJADA]

**Objetivo e benefício:** encontrar opções plausíveis antes de pedir à IA uma explicação.

**Dependências:** Etapas 2 e 4. Não confundir com a relevância textual da pesquisa implementada na Etapa 3.

**Entregas técnicas propostas:**

- Definir um pedido estruturado com interesses, restrições explícitas, jogo de referência, tempo opcional e contexto.
- Selecionar candidatos da biblioteca e do catálogo descoberto; estabelecer como buscar jogos ainda não possuídos
  sem importar toda a IGDB ou depender apenas dos jogos já cadastrados localmente.
- Priorizar a vontade atual sobre preferências históricas. Status e notas são sinais contextualizados,
  não proibições permanentes. Decidir como considerar finalizados, abandonados e jogos já em andamento.
- Separar até três recomendações de “Da sua biblioteca” e até três de “Para descobrir”.
  Não repetir um mesmo IgdbId nos dois grupos nem preencher vagas com opções inadequadas.
  Jogos ainda não possuídos podem aparecer em descoberta; recomendar não significa cadastrar.
  Nenhum jogo externo deve ganhar LibraryEntry ou posses sem uma ação explícita de cadastro do jogador.
- Definir pontuação simples, limiar de adequação e indicação de informação insuficiente.
  Manter originais, remakes e edições distintos e identificados.

**Critérios de conclusão:**

- [ ] Seleção determinística testável com gêneros, temas, status, notas e preferências explícitas.
- [ ] Cenários com poucos candidatos retornam menos de três, inclusive nenhum.
- [ ] Jogos externos sugeridos têm identidade verificável; nada é cadastrado ou comprado automaticamente.
- [ ] Ausência de tempo ou duração conhecida não impede recomendar; estimativas não são inventadas.

**Riscos e decisões:** critérios de exclusão, fonte de candidatos externos, custo de consulta,
qualidade do conjunto e preferência por biblioteca versus descoberta precisam ser aprovados.

## Etapa 6 — Recomendações por linguagem natural [PLANEJADA]

**Objetivo e benefício:** transformar uma intenção livre em sugestões justificadas, sem exigir filtros técnicos.

**Dependências:** Etapa 5; avaliação e aprovação de provedor antes da integração.

**Modos de recomendação planejados:**

- Busca rápida por linguagem natural, sem exigir formulários complexos.
- Busca detalhada com informações adicionais opcionais, sem tornar todos os campos obrigatórios.
- Possibilidade de refinar uma busca rápida com novas mensagens ou informações opcionais.
- Tempo disponível sempre opcional, inclusive quando informado naturalmente na mensagem, como
  “tenho meia hora para jogar”. Não exigir que o jogador repita esse dado em um campo separado.

Esses modos ainda não foram implementados. Em ambos, recomendar um jogo externo não o cadastra:
a inclusão na biblioteca depende de ação explícita do jogador e preserva a separação entre Game,
LibraryEntry e dados pessoais.

**Cenários de aceitação propostos:**

| Pedido | Comportamento esperado |
|---|---|
| “Quero um RPG com uma história emocionante.” | Identificar RPG e interesse narrativo, explicar sem revelar acontecimentos |
| “Tenho pouco tempo e quero jogar algo leve.” | Interpretar contexto, pedir esclarecimento apenas se necessário e não inventar duração |
| “Quero um jogo de terror e sobrevivência.” | Priorizar ambos os interesses explícitos |
| “Uma experiência parecida com Persona 5 Royal.” | Resolver o jogo de referência e explicar dimensões de semelhança, sem confundir versões |

**Entregas técnicas propostas:**

- Avaliar opções gratuitas ou de baixo custo com uma amostra fixa de pedidos em português.
  Gemini é candidata, não decisão aprovada nem integração existente.
  Confirmar preços, cotas, termos e políticas de dados vigentes no momento da escolha; gratuidade não é garantida.
- Separar interpretação do pedido, seleção fundamentada de candidatos e redação da explicação.
  O modelo não decide IDs válidos nem substitui as regras de persistência.
- Validar respostas estruturadas e aceitar somente candidatos conhecidos; rejeitar jogos inventados.
- Enviar apenas dados necessários. Não transmitir credenciais, notas pessoais ou histórico completo por padrão;
  definir consentimento para qualquer dado pessoal enviado ao provedor.
- Definir limites por pedido, tokens/contexto, orçamento, timeout, cancelamento, cache contextual seguro
  e tentativas finitas. Evitar que dados de perfis diferentes compartilhem respostas pessoais no futuro.
- Tratar textos externos e mensagens como dados não confiáveis; não executar instruções vindas do catálogo.
  Usar mensagens seguras em falhas e permitir continuar utilizando a biblioteca.

**Critérios de conclusão:**

- [ ] Provedor e orçamento aprovados; segredos somente no backend.
- [ ] Quatro cenários acima, conflitos com histórico e metadados insuficientes passam na avaliação.
- [ ] Até três opções por grupo, justificativas sustentadas por dados e ausência de spoilers nos cenários testados.
- [ ] Falhas, limites, JSON inválido e identidades inventadas tratados em testes sem chamadas reais.
- [ ] Validação real controlada e avaliação humana documentadas, sem afirmar que testes eliminam todos os riscos de IA.

**Riscos e decisões:** qualidade variável, vieses, exposição de dados, alucinações e custos.
Definir o que significa uma explicação aceitável e quais informações podem sair da máquina.

## Etapa 7 — Histórico, conversas e personalização progressiva [PLANEJADA]

**Objetivo e benefício:** refinar sugestões ao longo da conversa e evitar repetições pouco úteis.

**Dependências:** Etapa 6 com um primeiro fluxo estável.

**Entregas técnicas propostas:**

- Histórico opcional de conversas separado do histórico de jogos recomendados, com contexto mínimo necessário.
  Mensagens e refinamentos registram a conversa; o histórico de recomendações identifica os jogos e quando foram sugeridos.
- Conversas com refinamentos como “mais curto”, “menos difícil” ou “já joguei”.
- Registro de interesse positivo, como “Quero jogar esse!”, motivos opcionais para rejeitar sugestões
  e consideração das avaliações existentes. Demonstrar interesse não cadastra automaticamente um jogo na biblioteca.
- Diferenciar restrição desta conversa, preferência persistente e rejeição pontual.
  Uma rejeição isolada reduz adequação contextual; não cria bloqueio permanente.
  Um jogo rejeitado anteriormente poderá ser recomendado novamente quando fizer sentido para o pedido atual.
- Usar linguagem contextualizada para jogos já finalizados, abandonados ou anteriormente experimentados,
  reconhecendo a experiência anterior sem apresentá-los como novidade nunca jogada.
  A vontade atual do jogador tem prioridade sobre sinais históricos; preferências não viram bloqueios permanentes.
- Definir consentimento, retenção, exclusão, correção e resumo de histórico para limitar dados e contexto enviados.
- Tornar preferências inferidas revisáveis. “Aprendizado progressivo” começa como atualização de sinais;
  não pressupõe treinamento de modelo próprio ou ajuste fino.

**Critérios de conclusão:**

- [ ] Refinamentos mantêm a intenção relevante sem aprisionar o jogador ao pedido anterior.
- [ ] Vontade atual prevalece sobre histórico; rejeição pontual não elimina o jogo para sempre.
- [ ] Jogos anteriormente sugeridos são identificáveis, sem impedir reapresentação pertinente e explicada.
- [ ] Histórico pode ser limpo e preferências corrigidas; limites de armazenamento/contexto são testados.

**Riscos e decisões:** armazenamento padrão ou opt-in, retenção, sinais negativos, decaimento temporal
e tratamento de informações sensíveis ainda precisam de aprovação.

## Etapa 8 — Evolução da experiência e visual [PLANEJADA]

**Objetivo e benefício:** tornar descoberta, biblioteca e recomendações fáceis de compreender no uso contínuo.

**Dependências:** fluxo principal de 6 e evidências de uso; histórico visual depende de 7.
Correções de navegação e acessibilidade básica não precisam esperar essa etapa.

**Entregas técnicas propostas:** apresentar recomendações e justificativas de forma clara; histórico e filtros
simples quando úteis; navegação revisada, responsividade, teclado, foco e estados acessíveis.
Avaliar dashboard e identidade visual definitiva somente depois de estabilizar as funcionalidades.

**Critérios de conclusão:**

- [ ] Jornadas de busca, cadastro, recomendação e refinamento validadas no navegador com mouse e teclado.
- [ ] Telas pequenas, carregamento, erro e vazio tratados; nenhuma ação promete comportamento inexistente.
- [ ] Dashboard ou filtros só entram com problema de uso e benefício definidos.

**Riscos e decisões:** evitar retrabalho estético e complexidade desnecessária. Não há redesign imediato aprovado.

## Etapa 9 — Contas e bibliotecas individuais [A DEFINIR]

**Objetivo e benefício:** permitir uso por pessoas diferentes com dados pessoais isolados.

**Dependências:** decisão de expandir além do perfil local e desenho de identidade, autorização e migração.

**Entregas propostas:** cadastro e autenticação própria, resolução do perfil autenticado, autorização por recurso
e separação de biblioteca, histórico e preferências. A autenticação Twitch atual é da aplicação perante a API;
não autentica os jogadores. UserProfile existente é preparação parcial, não isolamento multiusuário implementado.

**Critérios de conclusão se aprovada:**

- [ ] Um usuário não lê, altera ou exclui dados de outro, inclusive manipulando URLs e IDs.
- [ ] Dados do perfil local migram de forma explícita, reversível e testada.
- [ ] Segredos da aplicação permanecem no servidor; sessões, recuperação de acesso e proteção contra abuso avaliadas.

**Riscos e decisões:** mecanismo de login, acesso público ou privado, recuperação, privacidade,
compartilhamento de catálogo e necessidade de trocar SQLite dependem do uso real.

## Etapa 10 — Publicação, operação e manutenção [A DEFINIR]

**Objetivo e benefício:** disponibilizar o produto fora da máquina de desenvolvimento com operação confiável.

**Dependências:** escopo de publicação aprovado; Etapa 9 antes de expor bibliotecas de usuários distintos.
O MVP F5 continua válido enquanto não houver essa decisão.

**Entregas propostas:** escolher hospedagem e banco a partir da carga; HTTPS, gestão de segredos do ambiente,
quotas compartilhadas, observabilidade sem dados sensíveis, atualizações, backup e restauração.
Planejar aplicação controlada de migrations: o startup automático atual em Development não é estratégia de produção.
Rever disponibilidade, termos de uso das APIs, custos e limites antes de publicar.

**Critérios de conclusão se aprovada:**

- [ ] Publicação em ambiente de teste e recuperação de backup demonstradas.
- [ ] Migrações e retorno de versão planejados sem perda de dados pessoais.
- [ ] Isolamento, monitoramento, alertas e tratamento de indisponibilidade validados.
- [ ] Orçamento e capacidade de manutenção compatíveis com operação externa.

**Riscos e decisões:** hospedagem, carga, escala, custo, política de logs/retenção e operação multi-instância.
Não há decisão de infraestrutura pública ou promessa de escalabilidade já entregue.

## Regras de evolução e aprovação

Decisões consolidadas: MVP local, MVC/.NET 10, SQLite/EF Core, catálogo IGDB, identidade por IgdbId,
cadastro manual preservado e separação entre catálogo e dados pessoais.
Diretrizes de produto deste planejamento: vontade atual prioritária, ausência de spoilers e até três opções adequadas por grupo.
Desenhos de dados da Etapa 4, pesos de recomendação, provedor de IA, retenção e expansão pública continuam propostas.

Antes de iniciar cada etapa futura:

- [ ] Aprovar escopo, dependências, modelo e decisões pendentes com o desenvolvedor.
- [ ] Definir cenários de aceitação e limites de custo/dados antes de implementar.
- [ ] Preservar alterações existentes e dados pessoais; testar mudanças de esquema em banco isolado.
- [ ] Executar validações apropriadas e registrar resultados, distinguindo testes simulados, reais e manuais.
- [ ] Atualizar este roadmap com evidências e limitações; não concluir uma etapa só porque o código compila.

Próxima ação recomendada: aprovar a investigação da Etapa 4 e escolher os metadados mínimos,
antes de adicionar campos, migrations ou integrações de IA.
