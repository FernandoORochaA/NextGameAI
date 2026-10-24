# 🎮 NextGame AI

**Sua biblioteca de jogos. Sua próxima grande aventura.**

O **NextGame AI** é uma aplicação web em desenvolvimento que combina gerenciamento de biblioteca pessoal, descoberta de jogos e, futuramente, recomendações personalizadas utilizando Inteligência Artificial.

A proposta é ajudar jogadores a organizarem seus títulos, registrarem suas experiências e encontrarem o próximo jogo ideal para cada momento.

O projeto também tem como objetivo o aprendizado prático de desenvolvimento Back-end e Front-end, integração com APIs externas, persistência de dados e testes automatizados.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C%23](https://img.shields.io/badge/C%23-ASP.NET_Core_MVC-512BD4)
![SQLite](https://img.shields.io/badge/SQLite-EF_Core-003B57?logo=sqlite&logoColor=white)
![IGDB](https://img.shields.io/badge/API-IGDB-9146FF)
![Status](https://img.shields.io/badge/Status-Em_desenvolvimento-yellow)

---

## 🚀 Funcionalidades implementadas

### 📚 Biblioteca pessoal

- Cadastro, edição, visualização e exclusão de jogos.
- Organização por status: **Não iniciado, Jogando, Finalizado e Abandonado**.
- Avaliações de 0 a 5 estrelas, incluindo meias estrelas.
- Distinção entre jogos não avaliados e avaliações de zero estrelas.
- Registro de observações pessoais e motivo opcional de abandono.
- Associação de um jogo a múltiplas plataformas: PC, PlayStation, Nintendo, Xbox e Outros.
- Proteção contra cadastros duplicados.
- Persistência local utilizando SQLite.

### 🔎 Integração com a IGDB

- Pesquisa de jogos reais diretamente pelo navegador.
- Exibição de capas, títulos, anos de lançamento, tipos e edições.
- Identificação individual de jogos originais, remakes, remasters e outras versões.
- Seleção de títulos encontrados na IGDB para cadastro na biblioteca.
- Identificação dos jogos por `IgdbId`, evitando duplicações.
- Cadastro manual disponível como alternativa.
- Autenticação segura via Twitch OAuth2, executada no backend.

### 🎯 Pesquisa por relevância

O NextGame AI possui uma classificação própria para melhorar a apresentação dos resultados da IGDB.

O sistema consulta até **30 candidatos** e apresenta os **10 mais relevantes**, considerando:

- Correspondência entre o título pesquisado e o nome do jogo.
- Correspondência de edições explicitamente procuradas.
- Preferência adicional para jogos principais e remakes.
- Normalização de maiúsculas, acentos e espaços.
- Preservação de versões alternativas e identificadores distintos.

A pesquisa também utiliza cache em memória, controle de requisições e tratamento de falhas da API externa.

---

## 🛠️ Tecnologias utilizadas

| Tecnologia | Utilização |
|---|---|
| C# / .NET 10 | Linguagem e plataforma principal |
| ASP.NET Core MVC | Backend, controllers e páginas Razor |
| Entity Framework Core 10 | Mapeamento de entidades e migrations |
| SQLite | Banco de dados local |
| HTML, CSS e JavaScript | Interface e pesquisa dinâmica |
| Bootstrap | Componentes e estrutura responsiva |
| Twitch OAuth2 | Autenticação da aplicação para a IGDB |
| IGDB API | Catálogo externo de videogames |
| xUnit | Testes automatizados |
| Git e GitHub | Versionamento do projeto |

---

## 📌 Status de desenvolvimento

**🔧 Projeto em desenvolvimento — MVP local funcional.**

### ✅ Etapas concluídas

- **Etapa 1 — Estrutura inicial:** aplicação MVC, organização da solução e infraestrutura de testes.
- **Etapa 2 — Biblioteca pessoal:** gerenciamento de jogos, avaliações, status, plataformas e persistência.
- **Etapa 3 — Integração IGDB:** autenticação, pesquisa, relevância, seleção e cadastro integrado.

### 🔜 Próximas etapas planejadas

- **Enriquecimento do catálogo:** obtenção e organização de gêneros, temas e características dos jogos.
- **Recomendações com IA:** interpretar pedidos em linguagem natural e sugerir jogos adequados ao contexto do jogador.
- **Personalização:** considerar avaliações, histórico e preferências pessoais.
- **Descoberta de jogos:** sugerir tanto títulos da biblioteca quanto alternativas ainda não adquiridas.
- **Histórico de recomendações:** permitir refinar sugestões e registrar o interesse do jogador.
- **Perfis individuais:** evoluir do usuário local para um sistema com autenticação própria.
- **Evolução visual:** desenvolver a identidade definitiva e aprimorar a experiência de uso.

As funcionalidades futuras ainda não estão implementadas e poderão ser ajustadas durante o desenvolvimento.

---

## 🤖 Visão futura — Recomendações inteligentes

A proposta central do NextGame AI é permitir que o jogador descreva o que deseja jogar em linguagem natural.

Por exemplo:

*"Quero jogar alguma coisa com uma história emocionante, um pouco de mistério e que não seja muito longa."*

A futura integração com IA deverá utilizar o catálogo, a biblioteca pessoal e as preferências do jogador para apresentar recomendações contextualizadas, acompanhadas de explicações sobre cada escolha.

O objetivo não é simplesmente listar jogos populares, mas **ajudar cada jogador a encontrar uma experiência que combine com o seu momento**, preservando o cuidado com spoilers.

**Esta funcionalidade faz parte do planejamento e ainda não está disponível.**

---

## 📖 Documentação técnica

Para detalhes sobre a implementação, consulte:

- [Biblioteca pessoal](docs/biblioteca.md)
- [Autenticação Twitch/IGDB](docs/autenticacao-igdb.md)
- [Serviços de pesquisa IGDB](docs/pesquisa-igdb.md)
- [Pesquisa visual](docs/pesquisa-visual-igdb.md)
- [Critérios de relevância](docs/relevancia-igdb.md)
- [Cadastro pela IGDB](docs/cadastro-igdb.md)

---

## ⚠️ Limitações atuais

O NextGame AI está em desenvolvimento e foi projetado inicialmente para execução local, utilizando um único perfil.

Ainda não possui autenticação própria de usuários, hospedagem pública ou recomendações com IA.

A disponibilidade dos resultados e das capas depende dos serviços externos da IGDB. O banco pessoal e as credenciais de desenvolvimento não fazem parte deste repositório.

---

## 👨‍💻 Autor

**Fernando Rocha**

Projeto pessoal de aprendizado e desenvolvimento de software.

[GitHub — FernandoORochaA](https://github.com/FernandoORochaA)

---

*NextGame AI — Porque o próximo jogo pode ser exatamente o que você precisava descobrir.* 🎮
