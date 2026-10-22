# Biblioteca pessoal — execução e validação

O cadastro e a edição agora usam o [formulário simplificado](formulario-simplificado.md), com estrelas e grupos de plataformas.

## Ambiente e execução

Requer SDK .NET 10 e Visual Studio 2026 atualizado, com a carga ASP.NET e desenvolvimento Web.
Visual Studio 2022 não oferece suporte oficial ao destino net10.0 deste projeto.

1. Abra `NextGameAI.slnx` no Visual Studio 2026.
2. Aguarde a restauração dos pacotes. Se necessário, defina `NextGameAI.Web` como projeto de inicialização.
3. Selecione o perfil `http` e pressione F5. A página inicial abre no navegador.
4. Clique em **Abrir minha biblioteca** ou **Minha biblioteca** no menu.

O perfil `https` também existe; seu uso pode solicitar confiança no certificado local de desenvolvimento.

No ambiente Development, o início da aplicação aplica automaticamente as migrations pendentes.
O primeiro início cria `src/NextGameAI.Web/nextgameai.db`, o perfil local, sete lojas/origens e seis plataformas.
Não são criados jogos de demonstração. Inícios seguintes não duplicam as referências.
O banco e os auxiliares SQLite estão ignorados pelo Git. Não apague o banco para atualizar a aplicação:
as migrations preservam e evoluem seus dados. Faça backup antes de futuras mudanças de esquema.

Para executar pela linha de comando, a partir da raiz:

```powershell
dotnet restore NextGameAI.slnx
dotnet build NextGameAI.slnx --no-restore
dotnet test NextGameAI.slnx --no-build
dotnet run --project src/NextGameAI.Web --launch-profile http
```

## Migrations

A migration `InitialLibrary` contém o esquema, índices, CHECKs e referências iniciais.
A ferramenta EF é fixada em `dotnet-tools.json`, alinhada aos pacotes EF Core 10.0.13.
Para aplicar manualmente (também funciona fora de Development):

```powershell
dotnet tool restore
dotnet ef database update --project src/NextGameAI.Web
dotnet ef migrations list --project src/NextGameAI.Web
dotnet ef migrations has-pending-model-changes --project src/NextGameAI.Web
```

O caminho padrão do banco é resolvido a partir da pasta do projeto web, tanto pelo F5 quanto pela ferramenta EF.
Uma configuração `ConnectionStrings:Library` pode substituir o caminho; não coloque credenciais em arquivos versionados.
Fora de Development as migrations não são aplicadas automaticamente.

## Modelo e decisões

- `Game` é catálogo compartilhado; `LibraryEntry` contém os dados pessoais de um `UserProfile`.
- `GameOwnership` relaciona cada entrada a uma loja/origem e uma plataforma; uma combinação não pode se repetir.
- `Store` e `Platform` são referências persistidas com códigos estáveis. Não há administração dessas referências nesta etapa.
- O perfil atual é resolvido por `CurrentUserProfile`; a identidade local fica centralizada em `LocalProfile`.
- A unicidade `(UserProfileId, GameId)` prepara bibliotecas distintas. Não há autenticação ou isolamento entre pessoas que usam o mesmo navegador/servidor local.
- `Rating` é inteiro anulável de 0 a 10, em unidades de meia estrela. A interface exibe de 0 a 5; vazio significa não avaliado.
- Status: 0 Não iniciado, 1 Jogando, 2 Finalizado, 3 Abandonado. O motivo é opcional e é limpo ao sair de Abandonado.
- O formulário principal cria um registro manual pelo título, sem seletor técnico de catálogo ou edição manual. Títulos iguais nunca são mesclados automaticamente.
- A operação interna `SaveAsync(LibraryForm)` mantém a reutilização por ID explícito e a rejeição de uma segunda entrada do mesmo jogo/perfil para futuros fluxos de catálogo.
- Na edição são alterados dados pessoais e posses; título/edição compartilhados e o vínculo com o catálogo são preservados.
- `EditionTitle` e os identificadores IGDB existentes são preservados no catálogo. A edição manual saiu do formulário; não existe hierarquia de versões ou integração IGDB nesta etapa.
- Excluir uma entrada remove suas posses, mas preserva catálogo e referências, inclusive para outro perfil ou reutilização futura.
- Cadastro e edição utilizam transação explícita. Falhas de gravação fazem rollback e limpam o rastreamento do contexto.
- Limites de texto são verificados no formulário e no DbContext, pois o SQLite não impõe os tamanhos declarados por HasMaxLength.
- Cada entrada representa somente posse efetiva; assinaturas, empréstimos, wishlist, IA e login estão fora do escopo.
- Novos cadastros usam a origem neutra `unspecified` e referências de plataforma genéricas. Posses detalhadas anteriores são preservadas, conforme o mapeamento documentado.

## Dependências necessárias

- `Microsoft.EntityFrameworkCore.Sqlite`: persistência e SQLite nativo.
- `Microsoft.EntityFrameworkCore.Design`: geração e aplicação de migrations; referência privada de desenvolvimento.
- `dotnet-ef` local: comandos de migration com versão reproduzível, sem atualizar a ferramenta global.
- Projeto de testes: xUnit, runner Visual Studio, Microsoft.NET.Test.Sdk e Microsoft.AspNetCore.Mvc.Testing.

## Validação automatizada

Os testes usam SQLite real em memória com migrations, chaves estrangeiras e CHECKs, sem mocks de persistência.
Cobrem cadastro, múltiplas posses, duplicatas, seleção de catálogo, perfil, notas, status, limites,
exclusão, integridade e rollback por falha forçada de banco no cadastro e na edição.
Os testes HTTP executam views e formulários reais por TestServer, incluindo erros e antiforgery,
sem gravar jogos no banco pessoal. Eles não executam JavaScript nem substituem uma inspeção visual no navegador.

A suíte inclui os testes originais e os novos cenários de formulário simplificado, preservação de dados legados,
grupos de plataformas e rollback das referências neutras. Os bancos de teste são isolados do banco pessoal.
Na última validação, os 110 testes foram aprovados.

## Roteiro manual pelo navegador

1. Abra a biblioteca e clique em cadastrar jogo.
2. Informe título, Jogando, observações e marque PC e PlayStation.
3. Clique na primeira metade da quarta estrela; confirme 3,5 de 5 estrelas. Salve e confira os detalhes.
4. Edite; as plataformas existentes devem estar marcadas e editáveis. Marque Xbox e salve.
   Desmarque PlayStation e confira o título "Confirmar remoção" e a mensagem
   "Tem certeza de que deseja remover PlayStation deste jogo?".
   Marque "Sim, desejo remover esta plataforma." e salve. O nome na mensagem acompanha a plataforma desmarcada;
   ao remover várias, confira e confirme cada uma separadamente.
   Remarcar PlayStation antes de salvar deve cancelar a remoção. Não é permitido remover todas as plataformas.
5. Teste Não avaliado, Zero estrelas, 0,5 e 5 estrelas, inclusive usando o teclado.
6. Selecione Abandonado e informe um motivo. Volte para Finalizado e confirme a limpeza.
7. No cadastro, não selecione plataformas; confira a mensagem e a preservação do preenchimento.
8. Em uma entrada antiga, confira que PS4/PS5 aparecem uma vez como PlayStation. As posses detalhadas seguem persistidas; lojas não são exibidas na listagem ou nos detalhes.
9. Reinicie pelo F5; confira persistência e ausência de duplicação de referências.

Com JavaScript desabilitado, os controles nativos de rádio e checkbox e a validação do servidor continuam ativos.
O preenchimento visual das estrelas é atualizado após enviar o formulário; o motivo de abandono permanece visível,
mas o servidor continua limpando-o quando o status não for Abandonado.
