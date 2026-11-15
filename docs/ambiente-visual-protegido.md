# Ambiente visual protegido — 4C.3A.1

O perfil `NextGameAI - Visual Seguro` seleciona `VisualDevelopment`. Sua existência
não autoriza F5 nesta etapa. O banco ainda não foi criado nem preparado.

## Destino e abertura

O único destino deriva de `Environment.SpecialFolder.LocalApplicationData`:
`NextGameAI/VisualDevelopment/nextgameai.visual.db`. Não há escolha livre de destino
nem fallback para o banco pessoal. A configuração exige `ConfigurationVersion=1`,
IGDB desabilitada e credenciais ausentes. Uma connection string externa, se presente,
deve corresponder ao formato estrito gerado pela aplicação; outras opções falham.

O caminho deve ser absoluto e local. Os componentes existentes não podem ser reparse
points (incluindo symlinks/junctions). Arquivo, marcador e auxiliares existentes devem
ter um único hard link. A identidade Windows é consultada por handles de metadados
com `FILE_READ_ATTRIBUTES`, sem abrir o SQLite para essa verificação.

A correção VD-01 consulta `GetDriveTypeW` na raiz do destino e permite somente
`DRIVE_FIXED` (3). Unidades remotas com letra, desconhecidas, indisponíveis,
removíveis, ópticas, RAM e falhas de classificação são recusadas. A consulta ocorre
na construção, na validação da conexão e novamente na preparação/abertura, antes
de consultar arquivos. Não se presume que uma letra corresponda a disco local.
Pontos de montagem de volume não são suportados: a verificação existente de todos
os componentes `ReparsePoint` os rejeita, evitando confiar na primeira letra para
um caminho que atravesse outro volume. A classificação é repetida após essa caminhada.
Os testes de tipos remotos usam a consulta substituída internamente, sem rede real.

`VisualDatabaseGuard` valida a conexão efetiva e a preparação nos interceptores
síncrono e assíncrono do EF. O modo é `ReadWrite`, não `ReadWriteCreate`; pooling é
desabilitado, cache privado e FKs habilitadas. Arquivos/diretórios ausentes ou
identidade não verificável são recusados. A guarda não intercepta conexões ADO.NET
criadas e abertas manualmente fora do contexto configurado; os caminhos atuais usam
o DbContext registrado. Novos caminhos devem preservar essa fronteira.

## Contrato de preparação futura

Antes do registro do contexto, a inicialização exige o arquivo e seu marcador
`nextgameai.visual.db.ready`. Seu conteúdo exato é `PreparationVersion`, uma quebra
de linha LF e a identidade `volume:fileIndexHigh:fileIndexLow` do arquivo.
`GetPreparationMarkerContents` apenas calcula esse conteúdo, não o grava.

A versão atual referencia a última migration existente. A 4C.3A.2 deverá criar
exclusivamente o destino aprovado, aplicar migrations e dados sintéticos e só então
gravar o marcador. A marca é um recibo local desse procedimento futuro, não prova
criptográfica nem auditoria do esquema/integridade do banco. Não se deve marcar um
arquivo arbitrário. Não existe reset ou migration visual automática. A preparação
explícita acrescentada na 4C.3A.2A é descrita abaixo e ainda não foi autorizada no destino real.

## Ambiente e HTTP

O perfil define os dois nomes de ambiente e um marcador de perfil. Antes de
`CreateBuilder`, combinações incompatíveis e argumentos que troquem o ambiente são
recusados, evitando carregar Development por engano nesse perfil. Os perfis normais
mantêm seu comportamento anterior, inclusive migrations em Development.

O ambiente visual usa diagnóstico local e mostra uma faixa sem caminhos/segredos.
A configuração final é validada, inclusive sobrescritas posteriores ao JSON.
User Secrets não é adicionado explicitamente e o ambiente não é Development.

`VisualHttpBarrier` substitui o transporte final de clientes `IHttpClientFactory`,
incluindo Twitch, IGDB e nomes não registrados. Nenhuma simulação é habilitada nesta
etapa. Futuras simulações precisam de checkpoint específico; não se deve retirar
a barreira para permitir rede real. Clientes manuais e tráfego do navegador não são
interceptados. Dados demonstrativos futuros devem usar capas locais ou sem capa.

## Limites e testes

Implementação específica para Windows, sem garantia absoluta contra substituição
maliciosa simultânea por processos com as mesmas permissões. Um marcador pode ser
forjado pelo mesmo usuário; ele não é um mecanismo contra esse adversário.

Os testes usam memória e arquivos sintéticos temporários, sem abrir SQLite
persistente. Junction e hard link são exercitados de fato. Symlink separado não é
criado nos testes; sua rejeição utiliza o mesmo atributo ReparsePoint verificado.
Testing, seus guardas e suas fixtures permanecem independentes e inalterados.

## Preparador explícito — 4C.3A.2A

O projeto console `tools/NextGameAI.VisualSetup` referencia o modelo e as migrations
web sem invocar `Program.cs`, criar host, ler User Secrets ou registrar HTTP/IGDB.
Sua simples execução não prepara nada: a entrada exige operação e confirmação
exatamente reconhecidas, além dos dois ambientes iguais a VisualDevelopment.
Não aceita caminhos, connection strings, reset ou opções adicionais. As instruções
operacionais de execução real ficam reservadas à 4C.3A.2B e autorização humana.

O destino continua derivado internamente de LocalApplicationData. A entrada interna
de testes exige diretório exclusivo sob a área temporária com prefixo
`NextGameAI-setup-test-`; não existe argumento público para selecionar essa raiz.

### Fases e propriedade do arquivo

`BeginPreparation` revalida unidade fixa e ancestrais, rejeita conflitos e cria
somente os dois diretórios filhos autorizados. Um semáforo nomeado Windows local
ao destino impede preparações simultâneas, inclusive entre processos da mesma sessão.
Criação por `FileMode.CreateNew` impede sobrescrita independentemente das verificações
anteriores. Banco, marcador, pendente ou auxiliares preexistentes causam recusa.

A autorização interna de preparação fica vinculada à identidade do arquivo recém-criado.
O interceptor revalida caminho, unidade, identidade e auxiliares antes das aberturas EF.
Ela não equivale a desabilitar guardas. A conexão permanece ReadWrite; somente dentro
dessa autorização admite-se também a sondagem ReadOnly que o EF SQLite utiliza ao
verificar a existência do banco antes de migrar. A guarda cotidiana continua estrita.

São aplicadas as três migrations existentes, sem transação externa envolvendo toda
a migração. A demonstração usa uma transação posterior. Não há novo modelo ou migration.
O preparador verifica o histórico exato, ausência de migrations pendentes, conteúdo
integral das doze tabelas de aplicação contra o conjunto esperado, foreign_key_check
e integrity_check. Tabelas auxiliares do EF/SQLite não são tratadas como dados pessoais.

Todos os recursos SQLite são encerrados antes da marcação. O marcador é escrito em
`.ready.pending` com criação exclusiva e flush, depois movido sem sobrescrita para
`.ready`, sem renomear o banco. Sua identidade final é a do arquivo efetivamente criado.
`RequirePrepared` valida o resultado. Se essa validação falhar, somente o marcador
com a identidade comprovadamente publicada pela operação pode ser removido.

Cancelamento é observado antes da publicação. Depois do ponto de publicação e da
validação final, um cancelamento tardio não transforma a conclusão em falso rollback.
Falhas anteriores não publicam marcador. Banco parcial e pendentes são preservados
para revisão explícita; não há limpeza automática ou recuperação sobre arquivos desconhecidos.
Uma segunda execução é recusada sem atualizar datas, migrar ou apagar alterações visuais.

### Demonstração

Quatro títulos inteiramente fictícios: Visual: Expedição Aurora, Visual: Oficina Orbital,
Visual: Jardim de Papel e Visual: Caminhos de Névoa. Cobrem conteúdo completo, parcial,
ausente e antigo com tentativa posterior malsucedida. Avaliações internas são 9, 0,
null e 6; possuem entradas/posses fictícias, incluindo motivo de abandono e loja fictícia.

Game.IgdbId e capas permanecem nulos. Os números positivos de taxonomias e plataformas
externas são placeholders sintéticos, sem afirmação de reserva ou inexistência na IGDB.
Há uma Keyword distinguível; ela continua excluída pela leitura de apresentação.
Fonte demonstrativa: VisualSynthetic. Um único instante T vem de TimeProvider;
sucessos recentes usam T−2 dias, antigos T−45 e tentativa malsucedida T−1.
Os dados envelhecem normalmente; reexecução não renova frescor.

### Validação e limites

Testes executam o preparador real, migrations e verificações apenas em arquivos
temporários próprios, com relógio controlado. Exercitam concorrência, falhas inclusive
durante SQL de migration, rollback, cancelamento, conflitos, identidade e leitura posterior.
Host=Mvc permanece Testing em memória. Nenhum teste usa o destino visual real ou pessoal.

A 4C.3A.2A não autoriza execução real, F5, reset, atualização de demonstração ou
auditoria automática. O console é ferramenta de desenvolvimento e não dependência
de execução do MVC. As limitações Windows, aliases e substituição simultânea descritas
acima continuam aplicáveis. Falhas são reportadas por etapa sem imprimir corpos,
credenciais ou connection strings.
