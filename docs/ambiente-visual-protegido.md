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

## Operação controlada — 4C.3A.2B

**A execução real continua sem autorização neste checkpoint**, inclusive a consulta
`check-synthetic` no destino definitivo. Os comandos abaixo são referência futura.
Não executar F5, MVC ou preparação antes da autorização específica posterior à auditoria.
Nenhum perfil de preparação com confirmação salva é criado.

### Sintaxe e pré-checagem

As duas operações exigem exatamente `DOTNET_ENVIRONMENT=VisualDevelopment` e
`ASPNETCORE_ENVIRONMENT=VisualDevelopment`, inclusive quando executadas fora do Visual Studio:

- `check-synthetic`: consulta somente metadados e espaço disponível; não exige confirmação de escrita.
- `prepare-synthetic --confirm-synthetic-only`: cria exclusivamente os dados sintéticos.

Outras operações, argumentos adicionais, caminhos, connection strings, reset, sobrescrita
e combinações de operações são recusados antes de resolver o destino. Não existe parâmetro
público de raiz alternativa. A entrada interna de testes permanece restrita à área temporária.

A pré-checagem deriva e mostra o caminho absoluto sob LocalApplicationData, exige armazenamento
local fixo, valida os ancestrais existentes e reconhece os seis conflitos: banco, `.ready`,
`.ready.pending`, `-wal`, `-shm` e `-journal`. Artefatos existentes passam pelas verificações
reais de reparse points e identidade/hard links. Diretórios ausentes são informados sem criação.
Uma inspeção recusada, espaço não consultável ou não positivo impede conclusão bem-sucedida.

Não há abertura de SQLite, leitura de conteúdo do banco, migration, publicação, reserva,
limpeza ou alteração de permissões durante `check-synthetic`. São reutilizadas as verificações
da guarda, sem uma segunda implementação de segurança. A disponibilidade de bytes é uma
fotografia informativa: não estabelece um tamanho mínimo garantido para a preparação.

**A pré-checagem não autoriza preparar, não reserva o destino e não garante permissões futuras
de escrita. Todas as guardas são executadas novamente durante a preparação.**

### Referência de execução futura — não executar agora

Após autorização própria, usar uma sessão PowerShell dedicada na raiz do repositório,
com a solução já compilada e validada. Não reutilizar essa sessão para iniciar o MVC:

```powershell
$env:DOTNET_ENVIRONMENT = 'VisualDevelopment'
$env:ASPNETCORE_ENVIRONMENT = 'VisualDevelopment'

dotnet run --project .\tools\NextGameAI.VisualSetup\NextGameAI.VisualSetup.csproj --no-build --no-restore --no-launch-profile -- check-synthetic
$LASTEXITCODE
```

Somente após autorização explícita de preparação, o comando de escrita seria:

```powershell
dotnet run --project .\tools\NextGameAI.VisualSetup\NextGameAI.VisualSetup.csproj --no-build --no-restore --no-launch-profile -- prepare-synthetic --confirm-synthetic-only
$LASTEXITCODE
```

A confirmação exigida é o argumento literal; não existe pergunta interativa. Fechar a sessão
após o procedimento evita herdar o ambiente visual em outras execuções. Não é necessário
alterar o projeto de inicialização do Visual Studio para usar esses comandos.

### Mensagens e códigos

O destino absoluto é mostrado antes de qualquer criação. O console não grava logs próprios,
não imprime connection strings, credenciais, exceções completas ou User Secrets.

- `0`: operação concluída; em check, apenas condições preliminares satisfeitas.
- `1`: argumentos/ambiente recusados, conflitos, acesso negado, E/S ou validação não confirmada.
- `2`: cancelamento observado.

As mensagens distinguem argumentos, ambientes, conflitos reconhecidos e permissão insuficiente.
Falhas de E/S indicam resultado indeterminado; a última etapa exibida localiza uma eventual
falha durante migrations, seed, verificação ou publicação. Erros de identidade/armazenamento
são recusados sem expor detalhes internos. Nenhuma mensagem recomenda desativar guardas.

### Publicação, interrupções e recuperação

Antes do movimento para `.ready`, pode existir banco parcial ou `.ready.pending`, mas a operação
ainda não publicou seu marcador final. Migrations não estão envolvidas em uma transação externa
única; o seed possui transação própria. Falhas não autorizam retomar sobre o arquivo existente.

O movimento exclusivo de `.ready.pending` para `.ready` é o ponto de publicação. Depois dele,
um cancelamento tardio não representa rollback. A validação real `RequirePrepared` continua
obrigatória; se falhar, somente o marcador com identidade comprovadamente pertencente à operação
pode ser removido. Um marcador substituído por terceiros é preservado, assim como o banco.

A preparação pode estar concluída mesmo se houver falha ao imprimir a mensagem final ou
encerramento inesperado do processo. Ausência de confirmação, código desconhecido ou código `1`
nessa situação exige inspeção independente. Não presumir que a operação pode ser repetida.

Banco existente, marcador inválido/ausente, pendente ou auxiliares conflitantes exigem parar e
preservar evidências. **Não apagar arquivos desconhecidos, fabricar marcador ou executar reset.**
Qualquer recuperação destrutiva exige investigação e autorização próprias. Reexecução é recusada
sem atualizar dados ou sobrescrever artefatos.

### Verificação posterior e futuro F5

Depois de uma execução real autorizada, conferir saída e etapas, revalidar marcador e identidade
e realizar inspeção independente autorizada do esquema, histórico de migrations, integridade,
FKs e conteúdo demonstrativo. A existência isolada de `.ready` não comprova essas condições.
Não usar migration como ferramenta de inspeção ou correção automática.

Preparar o banco não inicia o MVC. Para futuro F5, ainda será necessário aprovar o resultado
operacional, selecionar o projeto web e o perfil `NextGameAI - Visual Seguro`, confirmar conexão
ReadWrite, ausência de migrations automáticas, barreira HTTP, faixa sintética e ausência de capas
externas. A barreira não cobre tráfego do navegador nem clientes manuais. Development normal
continua com seu comportamento anterior; não utilizá-lo para validar o ambiente visual.

### Cobertura operacional

Os testes da entrada usam o mesmo despachante de Main com fábrica interna apontando somente
para a raiz temporária sintética. Argumentos/ambientes recusados não chegam a resolver essa raiz.
Nenhum teste chama Main ou RunAsync contra o destino definitivo; não há mudança global de ambiente.

Há cobertura de leitura sem escrita, conflitos com bytes inválidos para SQLite, armazenamento
remoto/indeterminado simulado, metadados indisponíveis, reparse points, hard links e espaço não
consultável. Os pontos internos de falha do marcador não são configuráveis pela linha de comando.
Exercitam criação, escrita parcial, flush, movimento, conflito tardio, validação real posterior,
propriedade do marcador, cancelamento na fronteira e falha da mensagem final. Falhas antes das
operações são injetadas em fronteiras específicas; não equivalem a simular queda de energia.
Todos esses cenários usam diretórios temporários próprios; a execução real permanece pendente.

Validação desta implementação: build sem erros ou avisos; 43 casos novos aprovados,
80 testes completos do preparador, 51 do ambiente visual, 749 Host=None e 89 Host=Mvc.
Total único: 838 aprovados, zero falhas e ignorados. Os subconjuntos não devem ser somados
novamente ao total das categorias. O resultado histórico anterior permanece em 795.
A aprovação de testes temporários não valida a execução no destino definitivo.
