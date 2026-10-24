# Pesquisa visual na IGDB

Execute NextGameAI.Web com F5 no Visual Studio 2026 e escolha “Pesquisar jogos na IGDB” no menu.
Também é possível abrir Minha biblioteca → Adicionar jogo.
Na pesquisa, “Não encontrou o jogo? Cadastrar manualmente” abre /Library/Create sem depender da IGDB.
A página fica em /Catalog (http://localhost:5032/Catalog ou https://localhost:7144/Catalog, conforme o perfil).
As credenciais continuam no User Secrets; nunca são enviadas ao navegador.

## Busca e resultados

A partir de 3 caracteres, a busca aguarda 400 ms sem digitação. Enter ou o botão Pesquisar iniciam imediatamente.
Requisições anteriores são canceladas e respostas antigas são ignoradas, inclusive durante a espera.
O endpoint GET /Catalog/Search?term=... valida de 3 a 100 caracteres e repassa o cancelamento ao IIgdbClient existente.
O cliente mantém seu cache de cinco minutos, limite de dez resultados exibidos e espaçamento entre chamadas.
Uma única chamada busca até 30 candidatos e os classifica localmente antes de retornar os dez melhores.
Veja [relevancia-igdb.md](relevancia-igdb.md) para pontuação e desempates.
O endpoint acrescenta um limite compartilhado de 30 consultas por minuto, sem fila, adequado ao MVP local de um usuário.
Esse limite inclui consultas atendidas pelo cache e entradas inválidas.

Cada jogo aparece com título, ano disponível, tipo traduzido quando conhecido, edição, identificador IGDB e capa.
Mesmo título com IDs diferentes permanece separado. Campos opcionais ausentes não impedem a exibição.
Capas são carregadas diretamente pelo navegador a partir do host fixo da IGDB, com lazy loading e texto alternativo.
Uma indicação de capa indisponível cobre ausência de imagem ou falha de carregamento.
Os textos externos são inseridos com textContent, sem interpretar HTML.

## Erros e segurança

A interface distingue início, espera, carregamento, lista vazia, indisponibilidade, falha de autorização,
limite de consultas e erro inesperado. Permite tentar novamente após falhas.
Respostas de erro contêm somente código e mensagem pública fixa, sem exceções, tokens ou respostas brutas.
O JSON de sucesso contém somente igdbId, title, releaseYear, gameType, editionTitle e coverUrl.
O controller não depende do DbContext e não acessa o SQLite.
Não há novos endpoints de autenticação, CORS ou gravação.

## Limitações

Requer JavaScript e conexão externa. Os candidatos e metadados dependem da IGDB;
a ordem final é definida pela relevância local, com ordem externa preservada somente nos empates.
Não há paginação. Cada resultado oferece “Adicionar à biblioteca”, que abre o formulário existente pelo IgdbId.
O cadastro manual permanece disponível. A identidade é resolvida no servidor e os dados pessoais são informados
no formulário. Veja [cadastro-igdb.md](cadastro-igdb.md) para as regras de persistência e o backup antes do F5.
O limite compartilhado deverá ser revisto antes de disponibilizar a aplicação para vários usuários.

Os testes automatizados da página e endpoint usam IIgdbClient simulado e SQLite em memória isolado,
sem depender da internet ou do banco pessoal. O comportamento no navegador deve ser validado separadamente.

Validação da etapa 3.4: restore e build concluídos, sem avisos ou erros; 250 testes aprovados,
incluindo os 223 anteriores e 27 novos casos MVC. O JavaScript também foi verificado com DOM simulado
para debounce, cancelamento, descarte de respostas antigas, textos externos, repetição, erros e nova tentativa.
Não foi realizado teste visual em navegador nesta etapa.

O trecho anterior registra a validação histórica da etapa 3.4. O desenvolvedor informou ter validado
posteriormente os fluxos no navegador. Na correção final da auditoria, a suíte chegou a 301 testes aprovados,
sem chamadas reais ou nova validação visual.
