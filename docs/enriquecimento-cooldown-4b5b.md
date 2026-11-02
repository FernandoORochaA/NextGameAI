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
