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
