# Cadastro da IGDB na biblioteca

## Migrations e backup

Faça backup do banco pessoal antes de abrir a aplicação. Program.cs já executa Database.Migrate()
automaticamente em Development, inclusive pelo F5. Durante a implementação, a migration foi gerada em Testing
e validada somente em SQLite isolado. Posteriormente, o desenvolvedor informou tê-la aplicado ao banco pessoal
durante a validação manual. A auditoria e suas correções não aplicaram migrations nem acessaram esse banco.
Se o banco estiver em uso, pare a aplicação antes de fazer a cópia; preserve também os arquivos auxiliares
existentes e não os apague.

A migration 20261024195821_IgdbCatalogMetadata adiciona quatro colunas opcionais a Games:
CoverImageId (até 128 caracteres), FirstReleaseDate (DateOnly), GameTypeId (zero ou positivo)
e GameType (até 100 caracteres). O Up usa somente AddColumn e preserva os IDs, índices, dados e relações.
IgdbId, Title e EditionTitle já existiam. Não se armazenam imagens binárias ou respostas brutas.
Os metadados novos não são obrigatórios para jogos manuais.

## Fluxo pelo F5

1. Após o backup, abra NextGameAI.slnx no Visual Studio 2026 e execute NextGameAI.Web em Development.
2. Na biblioteca, escolha “Adicionar jogo”, ou use “Pesquisar jogos na IGDB” no menu (/Catalog).
3. Pesquise um título e escolha “Adicionar à biblioteca” no resultado desejado.
4. Confira título, ano, tipo, edição, ID e capa disponíveis. Original e remake podem ter o mesmo nome:
   identifique-os pelo ano, tipo e IgdbId.
5. Preencha status, estrelas opcionais, observações e uma ou mais plataformas. Salve.
6. Confira os dados pessoais e os metadados nos detalhes e na biblioteca.
7. Repita com o mesmo resultado: deve aparecer uma mensagem com links para detalhes e edição,
   sem criar outra entrada ou sobrescrever os dados pessoais.
8. Na pesquisa, escolha “Não encontrou o jogo? Cadastrar manualmente”. O formulário manual (/Library/Create)
   continua independente da IGDB, inclusive em caso de indisponibilidade.

## Identidade, segurança e persistência

A ação usa apenas IgdbId para resolver a identidade. Título, edição e capa enviados pelo HTML são ignorados.
Primeiro busca Game pelo IgdbId local; somente se necessário usa GetByIdAsync, com os serviços,
cache e autenticação existentes. Um jogo local pode ser cadastrado mesmo se a IGDB estiver indisponível.

A consulta HTTP acontece antes da transação. Dentro da transação, Game é consultado novamente por IgdbId.
Se já existe uma LibraryEntry do perfil, nada é sobrescrito; se só existe Game, ele é reutilizado.
Um novo catálogo, a entrada pessoal e as referências/posses são gravados na mesma transação.
Falhas provocam rollback, inclusive de referências genéricas criadas durante a operação.
Conflitos de unicidade por criação concorrente são reavaliados após rollback, com uma única nova tentativa
usando o catálogo existente. Falhas esperadas de acesso (banco ocupado/bloqueado, somente leitura,
erro de E/S, disco cheio ou arquivo inacessível) mostram a mesma mensagem segura na preparação e no salvamento
do cadastro. O formulário mantém os dados pessoais e nenhuma gravação é iniciada se a preparação falhar.
SqliteException é filtrada por código; na gravação também é reconhecida quando encapsulada por DbUpdateException.
Erros inesperados da preparação e da gravação continuam sendo propagados aos diagnósticos normais.
As falhas EF tratadas no salvamento são concorrência e restrições SQLite, além das falhas de acesso citadas;
outras DbUpdateException não são convertidas indiscriminadamente em erros de validação.
Os índices únicos de IgdbId e de perfil/Game permanecem ativos.

Não há comparação por título para unir jogos. Jogos manuais antigos permanecem manuais e não são convertidos,
renomeados ou substituídos. Jogos externos com IDs diferentes permanecem independentes.
Edição e exclusão seguem as regras pessoais anteriores; excluir a entrada mantém o Game compartilhado.
As plataformas continuam usando os grupos e GameOwnership existentes, sem reintroduzir lojas na interface.
Zero estrelas é diferente de não avaliado; a escala persistida continua de 0 a 10.

Os formulários mantêm antiforgery. Razor codifica os textos e as capas usam somente o gerador seguro de URLs
da IGDB. User Secrets não são alterados nem enviados ao navegador.
Em caso de indisponibilidade, o formulário conserva os dados pessoais e mostra uma mensagem amigável;
a seleção externa nunca se transforma silenciosamente em cadastro manual.

## Validação e limites

Os testes usam o cliente IGDB real com HTTP/token simulados e SQLite isolado. Cobrem persistência, metadados,
reutilização, duplicação, original/remake, adulteração do HTML, falhas e rollback, migration com dados antigos,
edição/exclusão e preservação dos jogos manuais. Não houve chamadas reais nem teste visual nesta etapa.
Na validação histórica da etapa 3.5, restore e build concluíram sem avisos ou erros; 270 testes foram aprovados, incluindo os 250 anteriores
e 20 novos casos de integração e persistência. git diff --check e a verificação de sintaxe do JavaScript passaram.

A correção final da auditoria preservou os 289 testes acumulados e acrescentou 12 casos: seis códigos de
falha de leitura em GET/POST, rollback de gravação manual/IGDB e propagação de dois tipos de erro inesperado
tanto na preparação quanto na gravação.
Total atual: 301 aprovados, zero falhas; restore e build sem avisos ou erros.
As falhas são injetadas em comandos EF de SQLite em memória, sem bloquear ou acessar o banco pessoal.

Não há atualização automática de metadados já existentes, enriquecimento amplo, filtros avançados ou IA.
O catálogo local é a fonte preferencial após o primeiro cadastro. As capas dependem da disponibilidade do CDN.
