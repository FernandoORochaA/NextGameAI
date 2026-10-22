# Formulário simplificado da biblioteca

O formulário principal mostra título, status, avaliação, observações e plataformas; o motivo de abandono aparece
quando necessário. Não há campo de edição manual nem seletor técnico de catálogo.
O título permanece somente leitura na edição, pois pertence ao catálogo compartilhado, como na versão anterior.

## Mapeamento determinístico

| Seleção nova | Platform.Code | Platform.Name | Store.Code | Store.Name |
|---|---|---|---|---|
| PC | pc | PC | unspecified | Loja não informada |
| PlayStation | playstation | PlayStation | unspecified | Loja não informada |
| Nintendo | nintendo | Nintendo | unspecified | Loja não informada |
| Xbox | xbox | Xbox | unspecified | Loja não informada |
| Outros | other | Outros | unspecified | Loja não informada |

`pc` reutiliza a referência já existente. As demais referências e a origem neutra são procuradas pelo código estável
e criadas somente quando uma gravação realmente precisa delas, dentro da mesma transação da biblioteca.
Não alteramos o esquema, o seed original, as migrations existentes ou referências previamente cadastradas.
Nenhuma loja comercial, mídia física ou geração de console é inferida de uma seleção genérica.
Falhas fazem rollback inclusive das referências novas; salvar repetidamente não cria associações repetidas.

Para exibir posses anteriores, `PlatformGroups.FromPlatformCode` usa estes códigos exatos:

| Grupo exibido | Códigos conhecidos |
|---|---|
| PC | pc |
| PlayStation | playstation, ps1, ps2, ps3, ps4, ps5, psp, ps-vita |
| Nintendo | nintendo, switch, switch-2, wii, wii-u, 3ds, ds |
| Xbox | xbox, xbox-one, xbox-series, xbox-360 |
| Outros | other e qualquer código não reconhecido |

O agrupamento é apenas para a interface: PS4 e PS5 não são regravados como PlayStation.
Códigos desconhecidos aparecem em Outros, mantendo seu nome, identificador, loja e vínculos originais.

## Edição sem perda de informação

Todos os grupos representados pelas posses persistidas aparecem marcados e podem ser desmarcados no formulário.
O servidor relê as posses usando o ID da entrada e o perfil atual; não depende de IDs de loja/plataforma
enviados pelo navegador. Cada linha de um grupo mantido conserva seu ID, StoreId e PlatformId.
Grupos desmarcados exigem confirmação explícita antes que seus vínculos sejam removidos.
IDs IGDB e EditionTitle do catálogo também permanecem intactos.

Ao selecionar uma categoria ainda não representada, acrescentamos apenas uma posse neutra para ela.
Se já houver Steam/PC e GOG/PC, marcar PC não acrescenta uma terceira posse. Se houver PSN/PS4 e PSN/PS5,
a interface mostra PlayStation uma vez e as duas associações originais permanecem.

Ao desmarcar uma plataforma existente, o formulário mostra o título **"Confirmar remoção"**,
a mensagem **"Tem certeza de que deseja remover PC deste jogo?"** e o checkbox
**"Sim, desejo remover esta plataforma."** O nome PC é substituído dinamicamente pelo nome da plataforma.
Se várias plataformas forem desmarcadas, cada uma terá sua própria mensagem com o nome correspondente e seu checkbox.
É preciso marcar a confirmação de cada plataforma e salvar. Remarcar a plataforma cancela e limpa a confirmação.
O servidor calcula os grupos removidos a partir dos dados atuais e exige exatamente essas confirmações,
mesmo sem JavaScript ou em um pedido manipulado. Confirmações inválidas, repetidas ou extras são rejeitadas.
Sem JavaScript, a primeira tentativa de remover apresenta a confirmação na resposta, sem persistir mudanças.

São excluídas somente as linhas GameOwnership da entrada atual que pertencem ao grupo confirmado.
Game, LibraryEntry, Store e Platform continuam existindo. Outros jogos e perfis não são afetados.
Adicionar referências, remover vínculos e atualizar dados pessoais faz parte da mesma transação.
Validação ou falha de banco não deixa alterações parciais. A biblioteca continua exigindo ao menos um grupo.

**Limitações:** a remoção é por grupo, sem selecionar uma loja ou geração individual.
Desmarcar PlayStation, por exemplo, remove tanto PS4 quanto PS5 dessa entrada após confirmação.
Reincluir o grupo cria uma posse genérica, não restaura os detalhes dos vínculos removidos.
Listagem e detalhes exibem somente os grupos de plataformas, sem repetições ou nomes de lojas.
Uma futura edição detalhada poderá permitir remover uma posse específica mantendo outras do mesmo grupo.

## Catálogo e validação

Novos cadastros manuais criam um Game pelo título informado. Não há união automática por título;
duas entradas homônimas podem representar remakes ou registros diferentes.
A operação interna anterior continua aceitando um GameId explícito e rejeitando uma segunda entrada
do mesmo perfil para esse Game. O formulário simplificado não aceita campos técnicos de catálogo,
EditionTitle ou posses detalhadas por binding, e não acrescenta uma deduplicação heurística.

O servidor exige ao menos uma plataforma, rejeita categorias desconhecidas e repetidas, valida status,
limites de texto e Rating. Antiforgery continua ativo nos formulários. Nenhum pacote novo foi necessário.

## Avaliação acessível

São cinco estrelas, cada uma dividida em dois alvos com rótulos acessíveis (0,5 a 5 estrelas).
Rádios nativos mantêm navegação por Tab/setas e funcionamento sem JavaScript.
Não avaliado envia vazio e persiste null; Zero estrelas envia 0. As metades enviam inteiros de 1 a 10.
O preenchimento visual e o texto anunciado por aria-live acompanham a seleção.
O banco e seu CHECK continuam usando o mesmo inteiro anulável de 0 a 10.

## Testes e banco pessoal

Os testes usam SQLite em memória e a migration existente, incluindo dados legados com múltiplas lojas,
consoles diferentes, código desconhecido e edição/IGDB preenchidos. Verificam preservação dos IDs das posses,
duplicações, notas, rollback e o fluxo HTTP real. A inspeção de navegador deve usar banco temporário separado;
não é necessário cadastrar ou remover jogos no banco pessoal para validar esta mudança.

Na última validação da biblioteca, 110 testes passaram. Restore e build concluíram sem erros;
o build final não apresentou avisos. Os testes incluem remoção de grupos com múltiplas posses legadas,
preservação de outros jogos/perfis, notas/observações inalteradas, falha forçada durante a exclusão e confirmação HTTP.
No navegador, um banco temporário separado foi usado para conferir plataformas editáveis, exibição da confirmação,
bloqueio do envio sem confirmar e cancelamento ao remarcar o grupo. A remoção efetiva foi validada nos testes HTTP/SQLite.
O banco pessoal não foi usado para esses testes. O componente de estrelas não foi modificado.
