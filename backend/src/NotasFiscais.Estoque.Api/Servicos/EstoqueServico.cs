using System.Text.Json;
using NotasFiscais.Estoque.Api.Contratos;
using NotasFiscais.Estoque.Api.Dados;
using NotasFiscais.Estoque.Api.Dominio;
using NotasFiscais.Estoque.Api.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Estoque.Api.Servicos;

/// <summary>
/// Toda a regra de saldo mora aqui. Dois problemas dificeis sao resolvidos neste arquivo:
///
/// 1) CONCORRENCIA: duas notas disputando a ultima unidade. Resolvido com um UPDATE unico que
///    confere e subtrai no mesmo comando (ver <see cref="AplicarBaixaAsync"/>).
///
/// 2) IDEMPOTENCIA: o mesmo pedido chegando duas vezes (duplo clique ou retry automatico).
///    Resolvido com indice UNICO na chave de idempotencia + resposta gravada em banco.
/// </summary>
public class EstoqueServico(EstoqueDbContext db, ILogger<EstoqueServico> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Numeros de erro do SQL Server para violacao de indice unico.
    private const int ErroChaveDuplicada = 2627;
    private const int ErroIndiceUnicoDuplicado = 2601;

    // ------------------------------------------------------------------
    // Consulta de movimento por chave (usado pela idempotencia e pela
    // reconciliacao do Faturamento: "essa baixa chegou a acontecer?")
    // ------------------------------------------------------------------
    public async Task<MovimentoResposta?> BuscarPorChaveAsync(string chave, CancellationToken ct)
    {
        var mov = await db.Movimentos
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ChaveIdempotencia == chave, ct);

        if (mov is null) return null;

        return JsonSerializer.Deserialize<MovimentoResposta>(mov.RespostaJson, Json);
    }

    // ------------------------------------------------------------------
    // Baixa / estorno
    // ------------------------------------------------------------------
    public Task<MovimentoResposta> BaixarAsync(string chave, MovimentarEstoqueRequisicao req, CancellationToken ct)
        => MovimentarAsync(chave, TipoMovimento.Baixa, req, ct);

    public Task<MovimentoResposta> EstornarAsync(string chave, MovimentarEstoqueRequisicao req, CancellationToken ct)
        => MovimentarAsync(chave, TipoMovimento.Estorno, req, ct);

    private async Task<MovimentoResposta> MovimentarAsync(
        string chave, TipoMovimento tipo, MovimentarEstoqueRequisicao req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chave))
            throw ErroDeNegocioException.Invalido("CHAVE_IDEMPOTENCIA_OBRIGATORIA",
                "O header Idempotency-Key e obrigatorio nesta operacao.");

        if (req.Itens is null || req.Itens.Count == 0)
            throw ErroDeNegocioException.Invalido("ITENS_OBRIGATORIOS",
                "Informe ao menos um item para movimentar.");

        if (req.Itens.Any(i => i.Quantidade <= 0))
            throw ErroDeNegocioException.Invalido("QUANTIDADE_INVALIDA",
                "Toda quantidade deve ser maior que zero.");

        // Consolida o mesmo produto repetido no pedido e ORDENA por Id.
        // A ordenacao nao e estetica: duas transacoes concorrentes que travam os mesmos produtos
        // sempre na mesma ordem nao entram em deadlock (A espera B enquanto B espera A).
        var itens = req.Itens
            .GroupBy(i => i.ProdutoId)
            .Select(g => new ItemMovimentoRequisicao(g.Key, g.Sum(x => x.Quantidade)))
            .OrderBy(i => i.ProdutoId)
            .ToList();

        // Caminho rapido: ja processamos esta chave antes? Devolve a MESMA resposta, sem tocar no saldo.
        var jaProcessado = await BuscarPorChaveAsync(chave, ct);
        if (jaProcessado is not null)
        {
            log.LogInformation("Chave {Chave} ja processada, repetindo resposta (idempotencia).", chave);
            return jaProcessado with { Repetido = true };
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Grava o cabecalho ANTES de mexer no saldo. Como a chave tem indice unico,
            // dois pedidos simultaneos com a mesma chave nao conseguem passar daqui os dois:
            // o segundo fica bloqueado ate o primeiro commitar e entao recebe erro de duplicidade.
            var movimento = new MovimentoEstoque
            {
                ChaveIdempotencia = chave,
                Tipo = tipo,
                NotaFiscalId = req.NotaFiscalId,
                NotaNumero = req.NotaNumero,
                OcorridoEm = DateTime.UtcNow,
                RespostaJson = "{}"
            };
            db.Movimentos.Add(movimento);
            await db.SaveChangesAsync(ct);

            foreach (var item in itens)
                await AplicarBaixaAsync(tipo, item, ct);

            // Le os saldos resultantes de uma vez so (ja enxerga o que foi atualizado nesta transacao).
            var ids = itens.Select(i => i.ProdutoId).ToList();
            var produtos = await db.Produtos.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            var itensResposta = itens.Select(i =>
            {
                var p = produtos[i.ProdutoId];
                return new ItemMovimentoResposta(p.Id, p.Codigo, p.Descricao, i.Quantidade, p.Saldo);
            }).ToList();

            movimento.Itens = itensResposta.Select(i => new MovimentoItem
            {
                ProdutoId = i.ProdutoId,
                ProdutoCodigo = i.Codigo,
                Quantidade = i.Quantidade,
                SaldoResultante = i.SaldoResultante
            }).ToList();

            var resposta = new MovimentoResposta(
                movimento.Id, chave, tipo.ToString(), req.NotaFiscalId, req.NotaNumero,
                movimento.OcorridoEm, itensResposta);

            // Guarda a resposta para poder repeti-la identica se o pedido voltar.
            movimento.RespostaJson = JsonSerializer.Serialize(resposta, Json);
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            log.LogInformation("{Tipo} aplicada. Nota {Nota}, chave {Chave}, itens {Qtd}.",
                tipo, req.NotaNumero, chave, itens.Count);

            return resposta;
        }
        catch (DbUpdateException ex) when (EhViolacaoDeUnicidade(ex))
        {
            // Corrida: outro pedido com a MESMA chave chegou primeiro e ja commitou.
            // Isso nao e erro, e o caso feliz da idempotencia.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            var concorrente = await BuscarPorChaveAsync(chave, ct);
            if (concorrente is not null)
            {
                log.LogInformation("Chave {Chave} ganhou corrida em paralelo, repetindo resposta.", chave);
                return concorrente with { Repetido = true };
            }
            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// O comando mais importante do backend.
    ///
    /// A conferencia (Saldo >= quantidade) e a subtracao acontecem no MESMO comando SQL.
    /// Nao existe brecha entre "conferir" e "subtrair" para outra transacao se meter no meio,
    /// que e exatamente o cenario de concorrencia que o teste pede: duas notas, saldo 1.
    ///
    /// Se o UPDATE afeta 0 linhas, ou o produto nao existe ou o saldo acabou.
    /// </summary>
    private async Task AplicarBaixaAsync(TipoMovimento tipo, ItemMovimentoRequisicao item, CancellationToken ct)
    {
        int afetadas;

        if (tipo == TipoMovimento.Baixa)
        {
            afetadas = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE Produtos
                   SET Saldo = Saldo - {item.Quantidade},
                       AtualizadoEm = SYSUTCDATETIME()
                 WHERE Id = {item.ProdutoId}
                   AND Saldo >= {item.Quantidade}", ct);
        }
        else
        {
            // Estorno devolve saldo: nao precisa de guarda, so do produto existir.
            afetadas = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE Produtos
                   SET Saldo = Saldo + {item.Quantidade},
                       AtualizadoEm = SYSUTCDATETIME()
                 WHERE Id = {item.ProdutoId}", ct);
        }

        if (afetadas > 0) return;

        // Descobre por que nao afetou nada, para dar uma mensagem util em vez de "deu erro".
        var produto = await db.Produtos.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == item.ProdutoId, ct);

        if (produto is null)
            throw ErroDeNegocioException.NaoEncontrado(
                $"Produto {item.ProdutoId} nao existe.",
                new { produtoId = item.ProdutoId });

        throw ErroDeNegocioException.Conflito("SALDO_INSUFICIENTE",
            $"Saldo insuficiente para {produto.Codigo} ({produto.Descricao}). " +
            $"Disponivel: {produto.Saldo}, solicitado: {item.Quantidade}.",
            new
            {
                produtoId = produto.Id,
                codigo = produto.Codigo,
                descricao = produto.Descricao,
                saldoDisponivel = produto.Saldo,
                quantidadeSolicitada = item.Quantidade
            });
    }

    private static bool EhViolacaoDeUnicidade(DbUpdateException ex)
        => ex.InnerException is SqlException sql &&
           sql.Number is ErroChaveDuplicada or ErroIndiceUnicoDuplicado;
}
