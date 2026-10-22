# Notas Fiscais — microsserviços em .NET 8 e Angular

Sistema de emissão de notas fiscais com **Angular** no front e **C# / .NET 8** no back,
dividido em **dois microsserviços** com bancos separados e projetado para continuar se
comportando corretamente quando um dos lados fica indisponível.

> **Detalhamento técnico:** as escolhas de Angular, RxJS, bibliotecas, tratamento de erros e
> LINQ estão explicadas, com trechos do código, em
> **[docs/DETALHAMENTO_TECNICO.md](docs/DETALHAMENTO_TECNICO.md)**.

---

## Sumário

- [O que o sistema faz](#o-que-o-sistema-faz)
- [Arquitetura](#arquitetura)
- [Como rodar](#como-rodar)
- [Testes automatizados](#testes-automatizados)
- [Como ver a falha de um microsserviço acontecendo](#como-ver-a-falha-de-um-microsserviço-acontecendo)
- [Inteligência artificial](#inteligência-artificial)
- [Endpoints](#endpoints)
- [Estrutura de pastas](#estrutura-de-pastas)
- [Decisões de projeto](#decisões-de-projeto)

---

## O que o sistema faz

| Funcionalidade | Onde ver |
|---|---|
| Cadastro de produtos (código, descrição, saldo) | tela `/produtos` |
| Cadastro de notas fiscais (numeração sequencial, status Aberta/Fechada, vários produtos) | tela `/notas/nova` |
| Impressão com indicador de processamento: a nota vira Fechada e o saldo é baixado; só imprime nota Aberta | tela `/notas/{id}` |
| Dois microsserviços, Estoque e Faturamento, cada um com o próprio banco | `backend/src/` |
| Falha de um serviço com recuperação e explicação clara para quem está usando | [ver seção](#como-ver-a-falha-de-um-microsserviço-acontecendo) |
| Banco de dados real | SQL Server + EF Core, com migrations |

### Garantias além do básico

| Garantia | Como |
|---|---|
| **Concorrência** | A conferência e a subtração do saldo acontecem no mesmo comando SQL. Produto com saldo 1 disputado por várias notas: exatamente uma passa, o resto recebe `409`, e o saldo nunca fica negativo. |
| **Inteligência artificial** | "Montar nota por texto": você escreve o pedido em português e o sistema sugere os itens, casando com os produtos reais do cadastro. Funciona **sem chave de API**, em modo offline. |
| **Idempotência** | Todo pedido de baixa carrega um `Idempotency-Key`. Repetir o pedido (duplo clique ou nova tentativa automática) devolve a mesma resposta sem baixar o saldo de novo. |

---

## Arquitetura

```
                        ┌──────────────────────────────┐
                        │   Angular 22 (navegador)     │
                        │      localhost:4200          │
                        └───────┬──────────────┬───────┘
                                │              │
                  produtos/saldo│              │notas/impressão
                                ▼              ▼
        ┌───────────────────────────┐   ┌────────────────────────────┐
        │  Serviço de ESTOQUE       │   │  Serviço de FATURAMENTO    │
        │  localhost:5001           │◄──┤  localhost:5002            │
        │  .NET 8 + EF Core         │HTTP│  .NET 8 + EF Core + Polly │
        │                           │   │                            │
        │  Dono do SALDO.           │   │  Dono da NOTA.             │
        │  Só ele soma e subtrai.   │   │  Numera, fecha, reconcilia.│
        └────────────┬──────────────┘   └─────────────┬──────────────┘
                     │                                │
              ┌──────▼───────┐                 ┌──────▼────────────┐
              │ NotasEstoque │                 │ NotasFaturamento  │
              │ (SQL Server) │    ✗ nunca ✗    │  (SQL Server)     │
              └──────────────┘  cruzam bancos  └───────────────────┘
```

**Um banco por serviço.** Nenhum serviço lê ou escreve na base do outro: o Faturamento
**pede** a baixa ao Estoque por HTTP. Isso é o que faz deles microsserviços de fato, e não
dois pedaços do mesmo programa compartilhando tabelas.

### O fluxo da impressão

1. A tela chama `POST /notas/{id}/imprimir` com um `Idempotency-Key`.
2. O Faturamento confere se a nota está **Aberta**, marca **Processando** e guarda a chave.
3. Chama o Estoque em `POST /estoque/baixas`, levando a mesma chave.
4. O Estoque baixa o saldo dentro de uma transação, com conferência atômica.
5. Dando certo, a nota vira **Fechada** e a tela mostra os saldos novos.
6. Dando errado, o desfecho depende do motivo:
   - **saldo insuficiente** → nota volta para Aberta com o motivo gravado;
   - **Estoque indisponível** → o Faturamento **pergunta** se a baixa aconteceu antes de
     decidir qualquer coisa. Se não conseguir nem perguntar, a nota fica em Processando e é
     regularizada depois (no próximo clique ou pelo serviço de reconciliação em segundo plano).

O passo 6 é o coração do projeto: em falha, o sistema **não adivinha**.

---

## Como rodar

### Pré-requisitos

- [.NET SDK 8 ou superior](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org)
- Um SQL Server acessível. O padrão é o **LocalDB** que acompanha o Visual Studio /
  SQL Server Express (`(localdb)\MSSQLLocalDB`). Sem LocalDB, veja [Banco em container](#banco-em-container).

Não é preciso criar banco nem rodar script: as **migrations** criam o esquema e uma
carga inicial de produtos de exemplo na primeira execução.

### 1. Serviço de Estoque (porta 5001)

```bash
cd backend/src/NotasFiscais.Estoque.Api
dotnet run
```

### 2. Serviço de Faturamento (porta 5002)

Em outro terminal:

```bash
cd backend/src/NotasFiscais.Faturamento.Api
dotnet run
```

### 3. Front-end (porta 4200)

Em um terceiro terminal:

```bash
cd frontend/notas-web
npm install
npm start
```

Abra **http://localhost:4200**.

A documentação interativa das APIs fica em **http://localhost:5001/swagger** e
**http://localhost:5002/swagger**.

### Banco em container

Sem LocalDB (Linux, macOS, ou Windows sem SQL Server), suba um SQL Server com Docker:

```bash
docker compose up -d
```

E aponte os serviços para ele antes de `dotnet run`, no mesmo terminal:

```bash
$env:ConnectionStrings__Estoque = "Server=localhost,1433;Database=NotasEstoque;User Id=sa;Password=Notas@Local2026;TrustServerCertificate=True"
```

(no Faturamento, a variável é `ConnectionStrings__Faturamento`.)

---

## Testes automatizados

```bash
cd backend
dotnet test
```

**28 testes**, cerca de 5 segundos. Eles sobem **os dois serviços de verdade**, um
conversando com o outro por HTTP, contra um **SQL Server real**.

> O banco tinha que ser real. Metade do que estes testes provam (comando de UPDATE atômico,
> índice único, travamento de linha) simplesmente não existe num banco em memória: o teste de
> concorrência passaria sempre e não provaria nada.

Os que mais importam:

| Teste | O que garante |
|---|---|
| `Ultima_unidade_disputada_por_varias_notas_sai_uma_vez_so` | 8 pedidos simultâneos em saldo 1: um passa, sete recebem `409`, saldo final 0 |
| `Com_saldo_cinco_e_dez_pedidos_simultaneos_exatamente_cinco_passam` | O limite respeitado é o saldo, não a sorte |
| `Notas_criadas_ao_mesmo_tempo_nao_repetem_numero` | 10 notas em paralelo recebem números distintos e consecutivos |
| `Mesma_chave_disparada_varias_vezes_ao_mesmo_tempo_baixa_uma_vez_so` | Idempotência sob concorrência real |
| `Estoque_fora_do_ar_devolve_503_com_explicacao_e_nao_mexe_no_saldo` | Falha de um serviço não altera saldo nenhum |
| `Nota_nao_pode_ficar_fechada_com_saldo_intacto` | Trava a pior inconsistência possível do sistema |
| `Nota_presa_e_regularizada_sozinha_em_segundo_plano` | O reconciliador conserta sem ninguém clicar |

Para apontar os testes a outro servidor, defina `NOTAS_TEST_SQL` com a string de conexão.

---

## Como ver a falha de um microsserviço acontecendo

Há duas formas. A primeira é a mais realista, a segunda é a mais prática para repetir.

### Matando o processo

Feche o terminal do serviço de Estoque e clique em **Imprimir** numa nota. Repare que:

- a bolinha **Estoque** na barra do topo fica vermelha e piscando, antes mesmo de você clicar;
- a mensagem explica o que aconteceu, o que **não** aconteceu e o que fazer;
- **nenhum saldo foi alterado** e a nota não ficou Fechada;
- ao subir o Estoque de novo e clicar outra vez, a impressão conclui normalmente.

### Pelo interruptor de caos (só em Desenvolvimento)

```bash
curl -X POST "http://localhost:5001/admin/caos?ativo=true"    # derruba
curl -X POST "http://localhost:5001/admin/caos?ativo=false"   # religa
```

O serviço passa a responder `503` como se estivesse fora do ar, sem precisar fechar terminal.

> Ao voltar, a primeira tentativa ainda pode ser recusada: o **disjuntor** abriu por causa das
> falhas seguidas e leva alguns segundos para testar o serviço de novo. Isso é proposital, e a
> mensagem na tela diz exatamente isso.

---

## Inteligência artificial

Na tela **Nova nota**, o bloco *Montar por texto* aceita um pedido escrito em português
("3 canetas azuis, 2 cadernos e 1 grampeador") e devolve uma **sugestão** de itens.

Três cuidados de projeto:

1. **O servidor não confia no modelo.** A IA só pode escolher códigos do catálogo enviado, e
   ainda assim cada código é conferido contra o banco. Código inexistente é descartado.
2. **A IA sugere, a pessoa decide.** Nada é gravado até o clique em *Usar esta sugestão*.
3. **Funciona sem chave de API.** Sem chave configurada, um casamento local por semelhança de
   texto assume o trabalho e a tela avisa que está em **modo offline**.

Por isso este repositório roda por completo sem nenhuma configuração de IA.

### Ligando o Gemini (opcional)

```bash
dotnet user-secrets set "IA:ApiKey" "SUA_CHAVE" --project backend/src/NotasFiscais.Faturamento.Api
```

Ou defina a variável de ambiente `GEMINI_API_KEY`. A chave **nunca** vai para o repositório.
Com ela configurada, o selo na tela muda de *modo offline* para *IA · gemini-flash-lite-latest*.

---

## Endpoints

### Estoque — `localhost:5001`

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/produtos?busca=&pagina=&tamanho=` | Lista produtos com busca e paginação |
| `GET` | `/produtos/{id}` | Busca um produto |
| `POST` | `/produtos` | Cadastra um produto |
| `PUT` | `/produtos/{id}` | Atualiza descrição e saldo |
| `DELETE` | `/produtos/{id}` | Remove produto nunca movimentado |
| `POST` | `/estoque/baixas` | Baixa saldo (exige `Idempotency-Key`) |
| `POST` | `/estoque/estornos` | Devolve saldo (compensação) |
| `GET` | `/estoque/movimentos/{chave}` | "Essa baixa aconteceu?" |
| `GET` | `/health` | Saúde do serviço e do banco |
| `POST` | `/admin/caos?ativo=` | Simula indisponibilidade (só em Desenvolvimento) |

### Faturamento — `localhost:5002`

| Método | Rota | Descrição |
|---|---|---|
| `GET` | `/notas?status=&pagina=&tamanho=` | Lista notas |
| `GET` | `/notas/{id}` | Busca uma nota |
| `POST` | `/notas` | Cria nota com numeração sequencial |
| `POST` | `/notas/{id}/itens` | Adiciona produtos a nota aberta |
| `DELETE` | `/notas/{id}/itens/{itemId}` | Remove item de nota aberta |
| `DELETE` | `/notas/{id}` | Exclui nota não impressa |
| `POST` | `/notas/{id}/imprimir` | **Imprime**: baixa saldo e fecha a nota |
| `POST` | `/notas/interpretar` | Sugere itens a partir de texto livre |
| `GET` | `/health` | Saúde do serviço e do banco |

---

## Estrutura de pastas

```
notas-fiscais-microsservicos/
├── backend/
│   ├── NotasFiscais.sln
│   ├── src/
│   │   ├── NotasFiscais.Estoque.Api/          # dono do saldo
│   │   │   ├── Api/                   # endpoints
│   │   │   ├── Dados/                 # DbContext e migrations
│   │   │   ├── Dominio/               # entidades
│   │   │   ├── Infra/                 # erros, filtros, modo caos
│   │   │   ├── Servicos/              # regra de saldo (concorrência e idempotência)
│   │   │   └── Validacoes/
│   │   └── NotasFiscais.Faturamento.Api/      # dono da nota
│   │       ├── Clientes/              # cliente HTTP do Estoque + resiliência
│   │       ├── Servicos/              # numeração, impressão, reconciliação
│   │       └── Servicos/IA/           # montagem de nota por texto
│   └── tests/
│       └── NotasFiscais.Tests/                # 28 testes de integração
├── frontend/
│   └── notas-web/
│       └── src/app/
│           ├── nucleo/                # serviços HTTP, interceptores, modelos
│           ├── compartilhado/         # componentes reaproveitados
│           └── funcionalidades/       # telas de produtos e notas
├── docs/
│   └── DETALHAMENTO_TECNICO.md        # decisões técnicas explicadas com o código
├── docker-compose.yml                 # SQL Server em container (alternativa ao LocalDB)
└── README.md
```

---

## Decisões de projeto

**A numeração da nota não usa `IDENTITY`.** Ela vem de uma tabela de sequência incrementada
dentro da transação. O motivo apareceu sozinho durante o desenvolvimento: o `IDENTITY` do SQL
Server reserva blocos e **pula** depois de um reinício (uma nota ficou com `Id = 1007` logo
depois da de `Id = 9`). Número de nota fiscal com buraco não serve.

**A nota guarda uma foto do produto** (código e descrição no momento da emissão), e não só o
`ProdutoId`. É o correto em nota fiscal, e tem um efeito colateral bom: o Faturamento consegue
criar nota mesmo com o Estoque fora do ar. A dependência existe só na impressão, que é onde o
saldo realmente importa.

**A consulta de diagnóstico fica fora do disjuntor.** O disjuntor existe para parar de
martelar um serviço que está sofrendo, mas a pergunta "essa baixa aconteceu?" é justamente o
que se precisa quando as coisas quebraram. Atrás do mesmo disjuntor, uma nota presa
continuaria presa mesmo com o Estoque já recuperado.

**Datas sempre em UTC, marcadas como UTC.** O SQL Server guarda a data mas não o fuso; ao
voltar do banco ela chega sem marca e o navegador entende como hora local. O sintoma é
traiçoeiro porque é intermitente (certo vindo da memória, errado vindo do banco). Um conversor
aplicado a todas as datas do modelo resolve na origem.

---

Ana Flávia Oliveira das Neves ·
[Portfólio](https://ana-neves.vercel.app) ·
[LinkedIn](https://www.linkedin.com/in/anad3v/) ·
[GitHub](https://github.com/An4D3v)
