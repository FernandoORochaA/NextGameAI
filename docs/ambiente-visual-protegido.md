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
arquivo arbitrário. Não existe preparador, seed, reset ou migration visual automática.

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
