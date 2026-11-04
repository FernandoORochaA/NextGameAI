# Cooldown de enriquecimento — 4B.5B

Componente interno preparado para integração posterior pela 4B.5C. Não inicia consultas,
não modifica banco e não controla a execução dos métodos atuais de persistência.

`GameEnrichmentCooldown` é singleton, usa `TimeProvider` e a política de frescor da 4B.5A.
O chamador futuro deve registrar resultados somente após confirmação da transação;
falhas de persistência devem ser registradas somente após rollback. Cancelamento é neutro.
O instante informado deve identificar a tentativa, seguindo a convenção UTC do estado persistido.

| Resultado | Intervalo do MVP |
|---|---|
| Falha temporária, transporte, timeout ou HTTP | 15 minutos |
| Resposta inválida ou conflito semântico | 1 hora |
| EmptyPreserved | 24 horas |
| GameNotFound validado | 24 horas |
| HTTP 429 | Maior entre 15 minutos e Retry-After válido |
| Falha de persistência | 1 minuto, somente em memória |
| Cancelamento / NotRequested | Nenhum cooldown novo |

Esses intervalos são decisões do NextGame AI, não exigências da IGDB.
O transporte existente normaliza Retry-After relativo ou absoluto para `TimeSpan`.
O componente não modifica o scheduler compartilhado nem adiciona retries.

As decisões distinguem frescor, cooldown, inconsistência, origem e prazo. Exatamente no
limite, o cooldown termina. Um último sucesso recente ainda impede atualização até os 30 dias.
Falhas não renovam `LastSuccessAtUtc`. Estados incompatíveis ou futuros são bloqueados.

Detalhes em memória são isolados por GameId e seção, contêm apenas classificação e instantes
e só substituem o fallback persistido quando correspondem à mesma tentativa e resultado.
Falha de persistência pode corresponder a tentativa posterior ao estado que o rollback preservou.
Tentativas antigas não substituem entradas mais novas; sucesso confirmado invalida falhas anteriores.

O armazenamento tem limite de 1.024 entradas. Limpeza é oportunista em leitura/registro,
sem tarefa em background. Cooldowns expirados são removidos; marcas de sucesso duram 24 horas.
Ao atingir o limite, a entrada com tentativa mais antiga é removida. Não há retenção garantida
após remoção por capacidade, reinício ou término do processo.

Sem detalhe correspondente, TemporaryFailure usa LastAttemptAtUtc + 15 minutos, com indicação
explícita de motivo original desconhecido; NotFound usa 24 horas. Success não gera cooldown.
EmptyPreserved persiste como TemporaryFailure: suas 24 horas específicas **não sobrevivem
integralmente ao reinício ou à evicção**. Um Retry-After longo também perde seu detalhe após
reinício/evicção; o bloqueio próprio do scheduler permanece independente enquanto o processo vive.
Falhas de persistência não têm escrita compensatória nem fallback persistido próprio.

O componente não guarda payloads, keywords, tokens, headers, exceções, DbContext ou clientes HTTP.
Entidades e migrations permanecem inalteradas. Coordenação, concorrência e integração da execução
não fazem parte desta entrega.

## Evolução 4B.5C.2 — identidade e prazo fixo

`GameLease.CreateAttempt(section)` emite uma identidade com GameId, seção e sequência
monotônica compartilhada no processo, independente do relógio. A emissão exige lease ativo,
não confirma sucesso e não altera cooldown. O contador não dá a volta: esgotamento é rejeitado.
Não há persistência da sequência nem mudança nos serviços reais de enriquecimento.

`PrepareResult(identity, persistedAttemptAtUtc, clientCompletedAtUtc, result)` prepara uma
observação imutável, sem publicar no cache. O primeiro instante corresponde ao estado
persistido; o segundo deve ser capturado após a conclusão do cliente IGDB. O contrato atual
do transporte não expõe o instante original da resposta: datas HTTP absolutas já chegam
normalizadas como duração. A referência posterior pode prolongar conservadoramente a espera.

A observação contém o prazo absoluto UTC calculado uma única vez, incluindo o mínimo de
15 minutos para 429. Esperar pela escrita não reaplica a duração; reavaliar e republicar
a mesma observação não desloca seu prazo. Instantes locais ou futuros são rejeitados.
Não é possível verificar mecanicamente se o chamador capturou a referência depois do HTTP;
isso será responsabilidade da integração e de seus testes na 4B.5C.3.

`RecordConfirmed(observation)` deve ser chamado somente após commit confirmado, antes de
liberar o jogo. Preparar um sucesso ou emitir outra sequência não limpa cooldown anterior.
`RecordPersistenceFailure(identity, failedAtUtc)` registra somente um minuto em memória
após rollback. Cancelamento não publica observação; NotRequested não prepara observação.

Sequências antigas não substituem entradas mais recentes, mesmo com timestamp igual.
Entre falhas novas ou observações da mesma tentativa, preserva-se o maior prazo conhecido.
Uma confirmação de sucesso mais recente invalida falhas anteriores. Repetições da mesma
identidade devem manter timestamp persistido e referência temporal; alterações são rejeitadas.
A sequência não dispensa a correspondência com o estado persistido na avaliação.

As assinaturas antigas permanecem para compatibilidade, sem identidade monotônica.
Elas não substituem entradas identificadas já presentes e não devem ser usadas pela futura
integração temporal: seu parâmetro de timestamp ainda é também a referência do prazo relativo.
Em colisões de falhas legadas preserva-se o maior prazo conhecido.

O limite de 1.024 entradas, limpeza oportunista, marcas de sucesso por 24 horas e fallback
após reinício/evicção continuam válidos. Ordenação de resultados atrasados depende da marca
em memória ainda retida; após sua perda não há histórico durável de sequências.
O scheduler continua independente e inalterado. Nenhum retry HTTP ou SQLite foi criado.

## Integração 4B.5C.3

Os dois métodos públicos de `GameEnrichmentService` agora passam pelo mesmo núcleo coordenado.
O lease de GameId é adquirido antes da leitura. Frescor e cooldown são avaliados por seção;
somente seções elegíveis recebem identidade e entram em `GetSectionsAsync`. Sem seção elegível,
não há chamada externa, bloqueio global, transação de escrita ou tentativa fictícia.

O contexto de leitura é encerrado antes do cliente. A referência UTC é capturada depois que
o cliente retorna ou entrega uma falha tipada. Ela também é o timestamp destinado ao estado
persistido; observações ficam preparadas antes da espera pela escrita. Uma eventual classificação
semântica inválida descoberta ao persistir é preparada com os mesmos instantes, sem novo HTTP.

A ordem é jogo, escrita global e transação. Dentro da transação, identidade e estados são
recarregados e reavaliados. Mudanças em existência, timestamps, resultado ou Source descartam
a seção. A identidade é conferida novamente depois de SaveChanges. Um estado alterado e depois
restaurado com todos os mesmos campos (ABA) não é distinguível pelo esquema atual; a coordenação
impede isso nos caminhos normais da instância, mas não coordena processos externos.

Uma transação cobre as seções autorizadas. EmptyPreserved mantém seu comportamento conservador.
Somente depois de CommitAsync retornar com sucesso são publicadas as observações identificadas,
antes de liberar o jogo. Cancelamento depois desse ponto não oculta o commit confirmado.
Não há chamadas MVC automáticas nem alteração de dados pessoais.

Falhas de escrita antes do commit encerram os recursos transacionais e registram somente o
cooldown de persistência em memória. Cancelamento anterior ao commit propaga sem cooldown novo.
Exceção/cancelamento durante CommitAsync retorna `CommitIndeterminate`: não presume sucesso,
não publica observações e não repete a transação. O banco poderá precisar ser reavaliado em uma
nova solicitação; não existe recibo durável de tentativa para resolver todos esses casos.

Os resultados contêm decisões tipadas para recentes, cooldown, inconsistência, descarte e
resultados confirmados. `Sections` continua contendo somente resultados efetivamente persistidos.
Coordenador, cooldown, política e relógio mantêm os lifetimes existentes; não há retries SQLite,
mudança de transporte/scheduler, migrations ou sincronização entre processos.

## Recuperação 4B.5C.4

A consulta externa continua única. A fase de escrita admite uma tentativa inicial e, no máximo,
uma retentativa. O núcleo usa as mesmas identidades, resultados externos e observações com prazo
absoluto; não reinicia o enriquecimento nem recalcula Retry-After.

A classificação usa SqliteErrorCode e SqliteExtendedErrorCode, diretamente ou no InnerException
SQLite de DbUpdateException. Não usa mensagens. A lista conservadora de contenção recuperável é:

| Primário | Estendido aceito | Classificação |
|---|---|---|
| SQLITE_BUSY (5) | 5, BUSY_RECOVERY (261), BUSY_SNAPSHOT (517) | Busy |
| SQLITE_LOCKED (6) | 6, LOCKED_SHAREDCACHE (262) | Locked |

Outras extensões não listadas não recebem retry. FOREIGN KEY, CHECK, UNIQUE, PRIMARY KEY e outras
constraints não são repetidas; não há comprovação suficiente para tratar unicidade como disputa
recuperável de referência. Erros desconhecidos e erros de lógica também não são repetidos.

A retentativa exige falha anterior ao início de CommitAsync, rollback explícito bem-sucedido
quando há transação, descarte bem-sucedido de transação/contexto e liberação da escrita global.
Se a abertura da transação falhar, não houve fase de commit; os recursos adquiridos ainda precisam
ser encerrados. Qualquer falha de limpeza impede retry e fica indicada por CleanupCompleted=false.

Depois da limpeza há espera cancelável de 200 ms por TimeProvider, mantendo apenas o GameLease.
A segunda tentativa readquire escrita global e cria novo contexto e transação. Recarrega identidade,
estados e referências compartilhadas; estados alterados são descartados, sem novo HTTP.

Uma primeira falha recuperada não publica cooldown de persistência. Após falha final pré-commit,
somente o cooldown de persistência em memória é registrado. Cancelamento antes do commit, durante
a espera ou na segunda escrita não cria cooldown novo. Não há escrita compensatória.

O estágio de commit é explícito. Enquanto CommitAsync não retorna normalmente, exceções gerenciadas
relevantes, inclusive cancelamento e exceções fora das famílias de banco, geram CommitIndeterminate.
Dispor recursos ou observar rollback depois não prova que não houve commit. Não há retry nesse caso.
Exceções fatais identificadas (OutOfMemoryException, StackOverflowException, AccessViolationException,
inclusive encapsuladas) não são convertidas em resultados de recuperação.

Depois do retorno normal de CommitAsync, CommitState=Confirmed permanece preservado. A publicação
usa a fronteira pequena IGameEnrichmentPublisher, cujo adaptador normal chama RecordConfirmed.
Cada publicação é tentada uma vez, sem reiniciar HTTP ou transação. Uma falha gera
CommittedWithPublicationFailure; PublishedSections lista chamadas concluídas e PublicationFailedSections
lista chamadas que falharam. Uma chamada pode ter alterado o cache antes de lançar exceção: a lista
de falhas não afirma ausência de efeito. PublicationState distingue Complete, Partial e Failed.
As demais seções continuam sendo publicadas quando uma falha. Não existe reconciliação automática.

Falha gerenciada na limpeza após commit confirmado gera CommittedWithPostCommitFailure, preservando
o commit e a indicação de limpeza. Nenhum desses casos vira PersistenceFailure ou cooldown de escrita.
WriteAttempts informa tentativas de persistência (até duas), sem alterar os resultados por seção.

Os testes usam relógio/timers controlados, falhas injetadas e SQLite nomeado em memória com conexões
distintas. Há contenção real de um escritor externo ao coordenador. Exceções após confirmação SQLite,
mas antes do retorno observado pelo chamador, são injetadas por interceptores EF como proteção defensiva;
isso não afirma que todas as falhas reais do provider possam ser reproduzidas ou identificadas.
Um sinal interno de diagnóstico confirma registro efetivo na espera global; falhas gerenciadas do
observador não alteram a propriedade do semáforo. Esse sinal não expõe dados de jogos ao MVC.

Continuam os limites de uma instância, memória volátil, ausência de recibo durável e impossibilidade
de detectar ABA externo com todos os campos restaurados. Entidades, migrations, banco pessoal,
transporte IGDB, scheduler, controllers e Views não são alterados por esta entrega.

### Correções pós-auditoria da 4B.5C.4

A definição de fatal é compartilhada entre recuperação e diagnóstico da espera. A classificação
percorre iterativamente InnerException e todos os ramos de AggregateException.InnerExceptions,
incluindo wrappers e agregados aninhados. Referências já visitadas são reconhecidas por identidade,
evitando revisitas e ciclos. A lista de tipos fatais permanece a mesma.

A aquisição global usa cancelamento interno ligado ao solicitante. Se o observador propagar uma
falha, a espera já registrada é cancelada e sua conclusão é aguardada antes de encerrar a intenção
de escrita e propagar a exceção original. Se a aquisição tiver vencido a corrida, sua permissão é
liberada uma única vez; se a espera terminou cancelada, não há Release. O CTS local só é descartado
depois desse tratamento. Não há espera órfã ou continuação sem supervisão.

Falhas comuns do observador continuam neutras. A sinalização continua ocorrendo após registro
efetivo da espera; quando a escrita já está disponível, o observador de espera não é invocado.
Os testes usam objetos sintéticos de exceção fatal e sinais explícitos, sem provocar exaustão ou
afirmar recuperação segura do processo após uma falha fatal real. Retry e semântica de commit
permanecem inalterados.
