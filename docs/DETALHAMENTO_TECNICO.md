# Detalhamento técnico

Como as principais escolhas técnicas aparecem no código, com o caminho de cada arquivo.

**Stack:** Angular 22 (front) · C# / .NET 8 (back) · SQL Server + Entity Framework Core.

Índice:

1. [Ciclos de vida do Angular utilizados](#1-ciclos-de-vida-do-angular-utilizados)
2. [Uso de RxJS](#2-uso-de-rxjs)
3. [Outras bibliotecas e suas finalidades](#3-outras-bibliotecas-e-suas-finalidades)
4. [Bibliotecas de componentes visuais](#4-bibliotecas-de-componentes-visuais)
5. [Gerenciamento de dependências](#5-gerenciamento-de-dependências)
6. [Frameworks utilizados no C#](#6-frameworks-utilizados-no-c)
7. [Tratamento de erros e exceções no backend](#7-tratamento-de-erros-e-exceções-no-backend)
8. [Uso de LINQ](#8-uso-de-linq)

Notas sobre as decisões mais importantes do código:
[concorrência](#extra-a-concorrência-em-uma-linha-de-sql) ·
[idempotência](#extra-b-idempotência) ·
[falha entre serviços](#extra-c-o-que-acontece-quando-o-estoque-cai) ·
[IA](#extra-d-inteligência-artificial)

---

## 1. Ciclos de vida do Angular utilizados

Quatro ganchos, cada um resolvendo um problema concreto.

### `ngOnInit` — montar o fluxo de dados

Usado em `produtos-pagina.ts`, `notas-pagina.ts`, `nota-nova.ts` e `nota-detalhe.ts`.

É onde as inscrições nascem, e não no construtor: no construtor os `@Input` ainda não
chegaram, e efeito colateral em construtor atrapalha teste.

```ts
// produtos-pagina.ts
ngOnInit(): void {
  combineLatest([
    this.busca$.pipe(debounceTime(350), distinctUntilChanged()),
    this.paginacao$,
    this.recarregar$.pipe(startWith(undefined)),
  ])
    .pipe(switchMap(...), takeUntil(this.destruir$))
    .subscribe((pagina) => { ... });
}
```

### `ngOnDestroy` — encerrar as inscrições

Nos mesmos quatro componentes. Toda inscrição termina em `takeUntil(this.destruir$)`, e este
gancho dispara o sinal:

```ts
ngOnDestroy(): void {
  this.destruir$.next();
  this.destruir$.complete();
}
```

Sem isso, sair da tela no meio de uma busca deixaria a inscrição viva escrevendo em um
componente que não existe mais. É vazamento de memória e fonte de erro difícil de achar.

### `ngAfterViewInit` — ligar o paginador

Em `produtos-pagina.ts`. O `@ViewChild(MatPaginator)` só existe depois que a view foi criada,
então a inscrição no evento de paginação **não pode** ficar em `ngOnInit`:

```ts
@ViewChild(MatPaginator) private paginador?: MatPaginator;

ngAfterViewInit(): void {
  this.paginador?.page
    .pipe(takeUntil(this.destruir$))
    .subscribe((evento) => this.paginacao$.next(evento));
}
```

### `ngOnChanges` — reagir à mudança de entrada

Em `compartilhado/indicador-servico.ts`, o componente das bolinhas de saúde na barra do topo.
Ele não guarda estado de negócio: reage ao `@Input` mudando.

```ts
ngOnChanges(mudancas: SimpleChanges): void {
  if (!('noAr' in mudancas)) return;
  // recalcula cor e texto conforme o serviço caiu ou voltou
}
```

Concentrar isso aqui evita espalhar `*ngIf` de três estados pelo template do pai.

### Observação sobre o Angular 22: aplicação *zoneless*

O projeto **não usa `zone.js`** (é o padrão do Angular 22). Sem zone, a detecção de mudanças
não é disparada por *monkey patching* de eventos: o estado de tela é mantido em **signals**
(`signal`, `computed`), que avisam o Angular sozinhos quando mudam.

A divisão adotada foi:

- **RxJS** para o fluxo assíncrono (o que chega da rede, quando e em que ordem);
- **signals** para o estado que o template lê.

---

## 2. Uso de RxJS

Sim, e não como enfeite. Contagem real no código do front:

| Operador | Usos | Para quê |
|---|---|---|
| `takeUntil` | 17 | encerrar inscrições no `ngOnDestroy` |
| `switchMap` | 14 | trocar a requisição anterior pela nova |
| `catchError` | 12 | falha vira estado tratado, não fluxo morto |
| `Subject` / `BehaviorSubject` | 15 | fontes de busca, paginação e recarga |
| `startWith` | 6 | disparar a primeira carga |
| `debounceTime` | 5 | esperar a pessoa parar de digitar |
| `distinctUntilChanged` | 5 | não repetir chamada com o mesmo texto |
| `combineLatest` | 2 | juntar busca + paginação + recarga numa fonte só |
| `timer` | 3 | vigiar a saúde dos serviços de 5 em 5 segundos |
| `finalize` | 2 | contar requisições em andamento |
| `takeUntilDestroyed` | 3 | mesmo papel do `takeUntil`, em serviços |

### Busca de produtos: `debounceTime` + `distinctUntilChanged` + `switchMap`

```ts
combineLatest([
  this.busca$.pipe(debounceTime(350), distinctUntilChanged()),
  this.paginacao$,
  this.recarregar$.pipe(startWith(undefined)),
]).pipe(
  switchMap(([busca, pagina]) =>
    this.servico.listar(busca, ...).pipe(
      catchError((erro: ErroApi) => { this.aviso.erro(erro); return of(PAGINA_VAZIA); }),
    ),
  ),
  takeUntil(this.destruir$),
).subscribe(...);
```

Três decisões aqui:

- **`debounceTime`**: sem ele, cada tecla vira uma chamada HTTP.
- **`switchMap`**: se uma busca nova chega antes de a anterior responder, a anterior é
  **cancelada**. Sem isso, a resposta antiga poderia chegar depois e sobrescrever a nova na
  tela (o clássico problema de resultado fora de ordem).
- **`catchError` DENTRO do `switchMap`**: o erro derruba só aquela chamada. Se estivesse fora,
  o primeiro erro mataria o fluxo e a tela pararia de responder a qualquer busca seguinte.

### Vigilância de saúde: `timer` + `switchMap` + `catchError`

Em `nucleo/servicos/saude.service.ts`, alimenta as bolinhas verde/vermelha do topo:

```ts
timer(0, 5000).pipe(
  switchMap(() =>
    this.http.get(`${base}/health`, { responseType: 'text' }).pipe(
      map(() => true),
      catchError(() => of(false)),   // falha vira "fora do ar", o fluxo NÃO morre
    ),
  ),
  takeUntilDestroyed(this.destroyRef),
);
```

Efeito prático: quando o Estoque cai, a tela **avisa antes** de a pessoa clicar em Imprimir e
tomar erro.

### Autocomplete de produto na nota

Em `nota-nova.ts`, `valueChanges` do `FormControl` com `debounceTime(300)` +
`distinctUntilChanged()` + `switchMap`, com um detalhe: quando a pessoa **escolhe** uma opção,
o valor do controle deixa de ser texto e vira o objeto `Produto`. O `switchMap` trata os dois
casos, evitando uma busca inútil logo após a seleção.

### Interceptores

- `carregandoInterceptor` usa `finalize` para manter um **contador** de requisições em
  andamento (contador, não booleano: com booleano, a primeira chamada a terminar apagaria a
  barra de progresso das outras);
- `erroInterceptor` usa `catchError` + `throwError` para traduzir o erro HTTP cru no formato
  que a tela entende.

---

## 3. Outras bibliotecas e suas finalidades

### Backend (C#)

| Biblioteca | Versão | Finalidade |
|---|---|---|
| **Entity Framework Core** (`Microsoft.EntityFrameworkCore.SqlServer`) | 8.0.11 | Acesso a dados, mapeamento e **migrations** (o esquema nasce do código) |
| **Microsoft.Extensions.Http.Resilience** (Polly v8) | 8.10.0 | Resiliência da chamada entre serviços: novas tentativas com espera crescente, tempo limite e **disjuntor** |
| **FluentValidation** | 11.11.0 | Validação de entrada com a regra no C#, testável, devolvendo todos os erros de uma vez |
| **Serilog.AspNetCore** | 8.0.3 | Log estruturado, com contexto de rota, tempo e status |
| **Swashbuckle.AspNetCore** | 6.6.2 | Documentação interativa (Swagger) das duas APIs |
| **HealthChecks.EntityFrameworkCore** | 8.0.11 | `/health` que também confere o banco, e alimenta as bolinhas da tela |
| **xUnit** + **Microsoft.AspNetCore.Mvc.Testing** | 2.5.3 / 8.0.11 | Testes de integração subindo os dois serviços em processo |

**Deliberadamente ausentes:** nenhum AutoMapper (as projeções são explícitas em LINQ, o que
deixa visível o que vai para o banco) e nenhum MediatR (com dois serviços pequenos, ele
adicionaria camada sem remover complexidade).

### Front-end (Angular)

| Biblioteca | Finalidade |
|---|---|
| **Angular Material** + **CDK** | Componentes visuais (ver [item 4](#4-bibliotecas-de-componentes-visuais)) |
| **RxJS** | Fluxo assíncrono (ver [item 2](#2-uso-de-rxjs)) |
| `@angular/forms` | Formulários reativos no cadastro de produto e no autocomplete |
| `@angular/router` | Rotas com carregamento sob demanda e `withComponentInputBinding` |

Nenhuma biblioteca de estado (NgRx e afins): com signals e serviços, o estado deste tamanho
não justifica o peso.

---

## 4. Bibliotecas de componentes visuais

**Angular Material 22** (Material 3), com tema montado via `mat.theme()` em `styles.scss`.

Componentes usados:

| Componente | Onde |
|---|---|
| `MatToolbar` | barra do topo com navegação e saúde dos serviços |
| `MatTable` | listas de produtos, notas e itens |
| `MatPaginator` | paginação de produtos (ligada no `ngAfterViewInit`) |
| `MatFormField` + `MatInput` | todos os campos |
| `MatAutocomplete` | busca de produto ao montar a nota |
| `MatDialog` | cadastro e edição de produto |
| `MatSnackBar` | avisos de sucesso, atenção e erro |
| `MatProgressBar` | barra fina do topo enquanto há requisição no ar |
| `MatProgressSpinner` | **indicador de processamento da impressão** |
| `MatButtonToggle` | filtro de status das notas |
| `MatTooltip` | textos de apoio (inclusive o trecho que originou cada sugestão da IA) |
| `MatIcon` | ícones (Material Icons) |

O CSS próprio ficou restrito a layout e às cores de estado (saldo zerado, saldo baixo, status
da nota), usando as variáveis de tema do Material (`--mat-sys-*`) para não brigar com ele.

---

## 5. Gerenciamento de dependências

No C#, as dependências são declaradas por `PackageReference` nos arquivos
`.csproj`, restauradas pelo NuGet (`dotnet restore`, disparado automaticamente por
`dotnet build` e `dotnet run`), com **versão fixada explicitamente** em cada pacote para que a
compilação seja reprodutível.

---

## 6. Frameworks utilizados no C#

- **ASP.NET Core 8**, no estilo **Minimal API**. Os endpoints ficam agrupados por assunto em
  `Api/ProdutosEndpoints.cs`, `Api/EstoqueEndpoints.cs` e `Api/NotasEndpoints.cs`, registrados
  por métodos de extensão. Escolhido por ser menos cerimônia que Controllers para APIs deste
  tamanho, sem abrir mão de filtros e injeção de dependência.
- **Entity Framework Core 8**, com `DbContext` por serviço, configuração por `Fluent API`,
  migrations versionadas e `ExecuteSqlInterpolated` onde o SQL escrito à mão é a resposta
  certa (a baixa de saldo, ver [extra A](#extra-a-concorrência-em-uma-linha-de-sql)).
- **Filtros de endpoint** (`IEndpointFilter`) para rodar a validação antes do handler, sem
  repetir `if` em todo endpoint.
- **`BackgroundService`** para o reconciliador de notas presas.
- **Injeção de dependência nativa** do .NET, com `HttpClientFactory` para o cliente do Estoque.

---

## 7. Tratamento de erros e exceções no backend

### O princípio

Erro **previsto** (regra de negócio) e erro **imprevisto** (bug, banco fora) são coisas
diferentes e recebem tratamentos diferentes. Confundir os dois é o que produz aquele log cheio
de `ERROR` que ninguém lê mais.

### Uma exceção com significado

`Infra/ErroDeNegocio.cs` define `ErroDeNegocioException`, que carrega um **código estável**, o
status HTTP e detalhes estruturados:

```csharp
throw ErroDeNegocioException.Conflito("SALDO_INSUFICIENTE",
    $"Saldo insuficiente para {produto.Codigo} ({produto.Descricao}). " +
    $"Disponivel: {produto.Saldo}, solicitado: {item.Quantidade}.",
    new { produtoId = produto.Id, saldoDisponivel = produto.Saldo, ... });
```

Fábricas disponíveis: `NaoEncontrado` (404), `Invalido` (400), `Conflito` (409) e
`Indisponivel` (503).

### Um tradutor global, na saída

Um `IExceptionHandler` registrado com `app.UseExceptionHandler()` converte **qualquer** exceção
que escape para **ProblemDetails** (RFC 7807), o formato padrão de erro da web:

Resposta real de uma tentativa de impressão sem saldo:

```json
{
  "type": "https://notas-fiscais.local/erros/saldo_insuficiente",
  "title": "Saldo insuficiente para P-003 (Caderno universitario 96 folhas). Disponivel: 1, solicitado: 999.",
  "status": 409,
  "instance": "POST /notas/2008/imprimir",
  "codigo": "SALDO_INSUFICIENTE",
  "traceId": "0HNO25NULUPAN:00000001",
  "detalhes": {
    "notaId": 2008,
    "numero": 12,
    "statusDaNota": "Aberta",
    "origem": "Estoque",
    "detalhesDoEstoque": {
      "produtoId": 3,
      "codigo": "P-003",
      "saldoDisponivel": 1,
      "quantidadeSolicitada": 999
    }
  }
}
```

Repare em `statusDaNota: "Aberta"`: o erro já informa que a nota **não** ficou travada, e
`origem` diz de qual serviço veio a recusa.

O campo `codigo` é o contrato com a tela: ela decide o que mostrar pelo código, não por
comparar texto de mensagem. Nenhum *stack trace* chega ao usuário.

Casos tratados por tipo:

| Exceção | Vira | Observação |
|---|---|---|
| `ErroDeNegocioException` | o status que ela carrega | logada como **aviso**, sem stack trace |
| `BadHttpRequestException` | `400 REQUISICAO_INVALIDA` | parâmetro faltando ou JSON inválido é erro do cliente, não do servidor |
| `OperationCanceledException` | `499 REQUISICAO_CANCELADA` | o usuário fechou a aba |
| qualquer outra | `500 ERRO_INTERNO` | única que é logada como **erro**, com stack trace |

> Um erro de indisponibilidade tem status 5xx mas **é previsto**. Se a regra fosse
> "5xx = erro", um Estoque fora do ar encheria o log de `ERROR` com stack trace, escondendo
> os problemas de verdade. Por isso a decisão olha o **tipo** da exceção, não só o status.

### Validação de entrada

Um filtro de endpoint roda o validador do FluentValidation antes do handler e devolve **todos**
os erros de uma vez:

```json
{
  "status": 400,
  "codigo": "DADOS_INVALIDOS",
  "detalhes": [
    { "campo": "Codigo", "erro": "O codigo do produto e obrigatorio." },
    { "campo": "Saldo",  "erro": "O saldo inicial nao pode ser negativo." }
  ]
}
```

### Erro entre serviços

O `EstoqueClient` **não** usa `try/catch` genérico. Ele classifica a resposta em três desfechos
distintos, porque tratá-los igual seria o erro clássico:

```csharp
public abstract record ResultadoEstoque
{
    public sealed record Sucesso(MovimentoRetorno Movimento) : ResultadoEstoque;
    public sealed record RecusadoPeloNegocio(...) : ResultadoEstoque;  // 4xx: respondeu "não"
    public sealed record Indisponivel(string Motivo) : ResultadoEstoque; // não respondeu
}
```

"Saldo insuficiente" é resposta legítima do negócio; "o serviço caiu" é infraestrutura. Cada
um leva a nota a um estado diferente.

### Uma armadilha específica de .NET

```csharp
catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested is false)
```

`TaskCanceledException` **herda** de `OperationCanceledException`. Sem esse filtro, um tempo
limite do `HttpClient` seria confundido com "o usuário cancelou a requisição" e tratado como
se não fosse falha nenhuma.

### No front

O `erroInterceptor` traduz `HttpErrorResponse` para um formato único antes de chegar ao
componente, inclusive o `status: 0` (quando o navegador nem conseguiu falar com o servidor).
Nenhum componente lida com erro HTTP cru.

---

## 8. Uso de LINQ

**Sim**, em cerca de 50 pontos do backend, em três papéis distintos.

### a) Consultas traduzidas para SQL (LINQ to Entities)

O filtro, a ordenação e a paginação viram SQL: nada é trazido para a memória antes de filtrar.
O `Select` projeta direto no DTO, então a consulta não carrega colunas que ninguém vai usar
(como o carimbo de versão):

```csharp
// ProdutosEndpoints.cs
var consulta = db.Produtos.AsNoTracking().AsQueryable();

if (!string.IsNullOrWhiteSpace(busca))
    consulta = consulta.Where(p =>
        EF.Functions.Like(p.Codigo, $"%{termo}%") ||
        EF.Functions.Like(p.Descricao, $"%{termo}%"));

var total = await consulta.CountAsync(ct);

var itens = await consulta
    .OrderBy(p => p.Codigo)
    .Skip((nPagina - 1) * nTamanho)
    .Take(nTamanho)
    .Select(p => new ProdutoResposta(p.Id, p.Codigo, p.Descricao, p.Saldo, p.CriadoEm, p.AtualizadoEm))
    .ToListAsync(ct);
```

`AsNoTracking()` em toda leitura: sem intenção de alterar, não há motivo para o EF vigiar as
entidades.

Outros exemplos: `AnyAsync` para checar código repetido antes de inserir,
`ToDictionaryAsync` para ler os saldos resultantes de uma vez só, e a busca de notas presas do
reconciliador (`Where(...).OrderBy(...).Take(50)`).

### b) LINQ com efeito sobre a correção, e não só sobre o estilo

Em `EstoqueServico.cs`, antes de qualquer escrita:

```csharp
var itens = req.Itens
    .GroupBy(i => i.ProdutoId)
    .Select(g => new ItemMovimentoRequisicao(g.Key, g.Sum(x => x.Quantidade)))
    .OrderBy(i => i.ProdutoId)      // <<< isto não é estética
    .ToList();
```

- o `GroupBy` + `Sum` consolida o mesmo produto repetido no pedido;
- o **`OrderBy` evita deadlock**: duas transações concorrentes que travam os mesmos produtos
  sempre na mesma ordem não entram em espera circular (A esperando B enquanto B espera A).

### c) LINQ to Objects sobre coleções em memória

Montagem de respostas, consolidação das sugestões da IA, e a pontuação por semelhança de texto
do modo offline:

```csharp
var acertos = palavrasBusca.Count(b => palavrasProduto.Any(p => Combinam(b, p)));
```

---

## Extra A: concorrência em uma linha de SQL

O cenário mais delicado (produto com saldo 1 usado por duas notas ao mesmo tempo) é resolvido
por um único comando, onde a **conferência e a subtração acontecem juntas**:

```sql
UPDATE Produtos
   SET Saldo = Saldo - @qtd,
       AtualizadoEm = SYSUTCDATETIME()
 WHERE Id = @id
   AND Saldo >= @qtd;   -- a guarda
```

Se `@@ROWCOUNT` for 0, o saldo acabou: a operação vira `409 SALDO_INSUFICIENTE` e a transação
inteira é desfeita.

Isso é diferente de "consultar o saldo e depois subtrair": entre as duas operações existe uma
brecha onde outra transação cabe, e é exatamente ali que o saldo fica negativo.

Reforços em volta:

- tudo dentro de **uma transação**: numa nota com vários itens, ou todos saem ou nenhum sai;
- **`CHECK (Saldo >= 0)`** no banco, como segunda linha de defesa. Ao remover a guarda do
  `UPDATE` de propósito para testar, foi o `CHECK` que impediu o saldo negativo;
- **concorrência otimista** (`rowversion`) na *edição* do produto, para duas telas não
  sobrescreverem uma à outra em silêncio.

**Provado por teste:** 8 pedidos simultâneos em saldo 1 resultam em 1 sucesso, 7 conflitos e
saldo final 0. Com saldo 5 e 10 pedidos, exatamente 5 passam.

---

## Extra B: idempotência

Todo pedido de baixa carrega o header `Idempotency-Key`. A tabela `MovimentosEstoque` tem
**índice único** nessa chave e guarda a **resposta** dada:

1. chave já processada → devolve a **mesma resposta**, sem tocar no saldo;
2. duas requisições com a mesma chave chegando juntas → o índice único faz a segunda esbarrar
   na primeira; o erro de duplicidade é capturado e a resposta gravada é devolvida.

O detalhe que costuma faltar: guardar a **resposta**, e não só a chave. Devolver um `200` vazio
no segundo pedido deixaria a tela sem os saldos e mostrando estado errado.

No front, a chave é mantida **por nota** enquanto a impressão não terminar
(`notas.service.ts`), então até o clique repetido da pessoa é seguro.

---

## Extra C: o que acontece quando o Estoque cai

Fechar a nota (banco do Faturamento) e baixar o saldo (banco do Estoque) são duas escritas em
bancos diferentes, e não existe um `COMMIT` que cubra as duas. A solução tem quatro partes:

1. antes de chamar o Estoque, a nota vai para **Processando** e guarda a chave da tentativa;
2. a chamada leva essa chave, então repetir é seguro;
3. se o Estoque **recusa** (saldo insuficiente), a nota volta para Aberta com o motivo;
4. se a **resposta se perde**, o sistema não adivinha: **pergunta** ao Estoque se a baixa
   aconteceu (`GET /estoque/movimentos/{chave}`).
   - aconteceu → fecha a nota;
   - não aconteceu → devolve a nota para Aberta;
   - não deu nem para perguntar → a nota **fica** em Processando, e é resolvida no próximo
     clique ou pelo reconciliador em segundo plano.

O erro comum aqui seria devolver a nota para Aberta "por segurança" ao perder a resposta. Isso
pode deixar saldo baixado com nota aberta: o pior dos dois mundos.

**Política de resiliência** (`Program.cs`, configurável por `appsettings`): tempo limite de 2s
por tentativa, 2 novas tentativas com espera crescente e variação aleatória, e um **disjuntor**
que suspende as chamadas por 10s depois de metade das chamadas falhar numa janela de 30s.

Uma decisão contra-intuitiva: a consulta "essa baixa aconteceu?" usa um cliente HTTP **sem
disjuntor**. O disjuntor existe para não martelar um serviço que está sofrendo, mas essa
consulta é justamente o que se precisa quando algo quebrou, e é barata. Atrás do mesmo
disjuntor, uma nota presa continuaria presa mesmo com o Estoque já recuperado.

---

## Extra D: inteligência artificial

Funcionalidade *Montar nota por texto*: a pessoa escreve "3 canetas azuis e 2 cadernos" e
recebe uma **sugestão** de itens já casada com o catálogo real.

Três decisões:

1. **O servidor não confia no modelo.** Ele recebe apenas o catálogo e só pode escolher códigos
   dele; ainda assim, cada código volta a ser conferido contra o banco em
   `MontadorDeNotaServico`. Código inexistente é descartado e vira "não consegui identificar".
2. **A IA sugere, o humano decide.** Nada é gravado antes do clique em *Usar esta sugestão*.
3. **Funciona sem chave de API.** Sem chave, entra um casamento local por semelhança de texto
   (`InterpretadorOffline`), e a tela avisa que está em **modo offline**. Se a chamada ao
   modelo falhar por qualquer motivo, o mesmo caminho assume. A funcionalidade nunca quebra.

Provedor: **Google Gemini**, com `responseSchema` para a resposta vir em formato fixo em vez
de texto livre. A chave fica em *user-secrets* ou na variável `GEMINI_API_KEY`, nunca no
repositório.

---

## Testes automatizados

28 testes de integração que sobem **os dois serviços de verdade**, conversando por HTTP, contra
um **SQL Server real** — obrigatoriamente real, porque UPDATE atômico, índice único e
travamento de linha não existem num banco em memória.

A suíte foi validada por sabotagem: ao remover a guarda `AND Saldo >= @qtd` do UPDATE,
**11 testes quebram**. Teste que não sabe falhar não prova nada.

```bash
cd backend
dotnet test
```

---

Ana Flávia Oliveira das Neves ·
[LinkedIn](https://www.linkedin.com/in/anad3v/) ·
[GitHub](https://github.com/An4D3v)
