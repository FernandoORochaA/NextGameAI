# Relevância da pesquisa IGDB

A classificação é local, determinística e aplicada em IgdbClient.SearchAsync antes do cache.
Uma única consulta solicita até 30 candidatos com os mesmos campos de metadados existentes.
Após validar a resposta e deduplicar somente IDs idênticos, os candidatos são classificados e os dez primeiros retornam.
Não há consultas adicionais por candidato, filtros por tipo ou remoção de version_parent.
O limite de dez implica que candidatos de menor pontuação podem ficar fora da apresentação; não há paginação nesta etapa.

## Pontuação

Título e consulta são normalizados para comparação: maiúsculas invariantes, remoção de acentos,
pontuação tratada como separação de palavras e espaços consecutivos reduzidos. Os títulos exibidos não são alterados.

- Título normalizado exatamente igual à consulta: 1000 pontos.
- Título mais edição exatamente igual à consulta: 950 pontos.
- Todas as palavras distintas da consulta presentes no título: 700 pontos, mais 40 por prefixo completo
  e até 30 pela proporção de palavras correspondentes.
- Algumas palavras presentes: 300 vezes a fração de palavras correspondentes, mais o bônus de prefixo.
- Apenas trecho textual sem palavra completa: 100 pontos; nenhuma correspondência: zero.
- A correspondência com título mais edição também é considerada, com desconto de 50 pontos quando não é exata.
  Usa-se o melhor resultado entre título e título mais edição.

As palavras são comparadas como tokens, sem regras por franquia, remoção de números ou dicionários de stopwords.
A soma adicional de tipo é pequena para que não supere uma correspondência textual específica:

| game_type.id | Tipo | Pontos adicionais |
|---|---|---|
| 0 | Main Game | 40 |
| 8 | Remake | 38 |
| 9 | Remaster | 28 |
| 10 | Expanded Game | 26 |
| 11 | Port | 20 |
| 4 | Standalone Expansion | 15 |
| 1, 2 | DLC / Expansion | 5 |
| 3, 13 | Bundle / Pack | 0 |
| Ausente, desconhecido ou outro | Mantido no conjunto | 10 |

Os códigos são mapeados explicitamente, sem interpretar a ordem numérica como relevância.
A documentação oficial identifica game_type como referência de Game Type e category como obsoleto:
https://api-docs.igdb.com/#game e https://api-docs.igdb.com/#game-type.
Os tipos 0, 8, 9, 10 e 13 também foram observados nas respostas reais desta validação.

Empates mantêm a ordem original da IGDB, via ordenação estável. Ano, popularidade e perfil pessoal não alteram a pontuação.
IDs distintos continuam separados, inclusive quando o título coincide. GetByIdAsync continua sendo uma consulta por identidade.
Cache de cinco minutos, limites de resposta de 256 KiB, cancelamento, controle de chamadas e tratamento de erros são preservados.

## Validação

Na validação histórica da etapa 3.6, restore e build concluíram sem avisos ou erros; 289 testes foram aprovados, preservando os 272 anteriores.
Os testes existentes de consulta foram ajustados para limit 30 e tipos coerentes no cenário de empate.
Os 17 casos novos cobrem normalização, correspondência parcial/exata, VR, edição, tipos, empates, candidatos,
limite final, cache e identidade selecionada. Todos usam dados simulados.
A correção posterior da auditoria não alterou a relevância e elevou a suíte para 301 testes aprovados.

Separadamente, foram realizadas duas pesquisas reais controladas em 24/10/2026:

- Resident Evil 4: os primeiros resultados foram 974 (original, 2005), 132181 (remake, 2023),
  20065 (remaster, 2011), 145191 (expanded game, 2005) e 172560 (VR, 2021).
- Persona 5 Royal: o primeiro foi 114283 (2019), seguido de 134222 (Launch Edition, 2020),
  116559 (Limited Edition, 2019), 261539 (1 More Edition, 2023) e 134241 (Straight Flush Edition, 2019).

Ambas responderam HTTP 200 e retornaram dez itens. Repetições no mesmo processo não fizeram novas chamadas externas.
O diagnóstico temporário foi removido. Não houve acesso ao SQLite pessoal ou teste visual no navegador.

## Limitações

A classificação só conhece os candidatos retornados pela IGDB; não garante recuperar títulos ausentes desse conjunto.
Metadados incorretos ou incompletos podem afetar a prioridade. A normalização não resolve sinônimos,
abreviações, transliterações ou algarismos romanos. Tipos desconhecidos continuam visíveis sem inferência pelo nome.
Futuras melhorias poderão considerar popularidade, preferências pessoais e relevância contextual,
sem que esta etapa implemente personalização ou recomendações com IA.
