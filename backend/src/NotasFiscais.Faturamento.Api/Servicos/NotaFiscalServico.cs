using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Contratos;
using NotasFiscais.Faturamento.Api.Dados;
using NotasFiscais.Faturamento.Api.Dominio;
using NotasFiscais.Faturamento.Api.Infra;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Faturamento.Api.Servicos;

public class NotaFiscalServico(
    FaturamentoDbContext db,
    EstoqueClient estoque,
    ILogger<NotaFiscalServico> log)
{
    public static NotaResposta Mapear(NotaFiscal n) => new(
        n.Id, n.Numero, n.Status.ToString(), n.CriadaEm, n.ImpressaEm, n.UltimoErro,
        n.Itens.OrderBy(i => i.Id)
               .Select(i => new ItemNotaResposta(i.Id, i.ProdutoId, i.ProdutoCodigo, i.ProdutoDescricao, i.Quantidade))
               .ToList());

    // ------------------------------------------------------------------
    public async Task<PaginaResposta<NotaResposta>> ListarAsync(
        string? status, int? pagina, int? tamanho, CancellationToken ct)
    {
        var nPagina = pagina is null or <= 0 ? 1 : pagina.Value;
        var nTamanho = tamanho is null or <= 0 or > 200 ? 20 : tamanho.Value;

        var consulta = db.Notas.AsNoTracking().Include(n => n.Itens).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<StatusNota>(status, true, out var st))
            consulta = consulta.Where(n => n.Status == st);

        var total = await consulta.CountAsync(ct);

        var notas = await consulta
            .OrderByDescending(n => n.Numero)
            .Skip((nPagina - 1) * nTamanho)
            .Take(nTamanho)
            .ToListAsync(ct);

        return new PaginaResposta<NotaResposta>(notas.Select(Mapear).ToList(), total, nPagina, nTamanho);
    }

    // ------------------------------------------------------------------
    public async Task<NotaFiscal> CarregarAsync(int id, CancellationToken ct, bool rastrear = true)
    {
        var consulta = db.Notas.Include(n => n.Itens).AsQueryable();
        if (!rastrear) consulta = consulta.AsNoTracking();

        return await consulta.FirstOrDefaultAsync(n => n.Id == id, ct)
            ?? throw ErroDeNegocioException.NaoEncontrado($"Nota {id} nao encontrada.", new { notaId = id });
    }

    // ------------------------------------------------------------------
    public async Task<NotaResposta> CriarAsync(CriarNotaRequisicao req, CancellationToken ct)
    {
        var itens = Consolidar(req.Itens);
        await ConferirProdutosAsync(itens, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var numero = await ProximoNumeroAsync(ct);

        var nota = new NotaFiscal
        {
            Numero = numero,
            Status = StatusNota.Aberta,
            CriadaEm = DateTime.UtcNow,
            AtualizadaEm = DateTime.UtcNow,
            Itens = itens.Select(i => new ItemNota
            {
                ProdutoId = i.ProdutoId,
                ProdutoCodigo = i.Codigo,
                ProdutoDescricao = i.Descricao,
                Quantidade = i.Quantidade
            }).ToList()
        };

        db.Notas.Add(nota);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Nota {Numero} criada com {Qtd} item(ns).", nota.Numero, nota.Itens.Count);
        return Mapear(nota);
    }

    // ------------------------------------------------------------------
    public async Task<NotaResposta> AdicionarItensAsync(int id, AdicionarItensRequisicao req, CancellationToken ct)
    {
        var nota = await CarregarAsync(id, ct);
        GarantirAberta(nota);

        var novos = Consolidar(req.Itens);
        await ConferirProdutosAsync(novos, ct);

        foreach (var item in novos)
        {
            var existente = nota.Itens.FirstOrDefault(i => i.ProdutoId == item.ProdutoId);
            if (existente is not null)
            {
                existente.Quantidade += item.Quantidade;
                continue;
            }

            nota.Itens.Add(new ItemNota
            {
                ProdutoId = item.ProdutoId,
                ProdutoCodigo = item.Codigo,
                ProdutoDescricao = item.Descricao,
                Quantidade = item.Quantidade
            });
        }

        nota.AtualizadaEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Mapear(nota);
    }

    // ------------------------------------------------------------------
    public async Task<NotaResposta> RemoverItemAsync(int id, int itemId, CancellationToken ct)
    {
        var nota = await CarregarAsync(id, ct);
        GarantirAberta(nota);

        var item = nota.Itens.FirstOrDefault(i => i.Id == itemId)
            ?? throw ErroDeNegocioException.NaoEncontrado($"Item {itemId} nao existe nesta nota.");

        nota.Itens.Remove(item);
        db.ItensNota.Remove(item);
        nota.AtualizadaEm = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return Mapear(nota);
    }

    // ------------------------------------------------------------------
    public async Task ExcluirAsync(int id, CancellationToken ct)
    {
        var nota = await CarregarAsync(id, ct);

        if (nota.Status == StatusNota.Fechada)
            throw ErroDeNegocioException.Conflito("NOTA_FECHADA",
                $"A nota {nota.Numero} ja foi impressa e nao pode ser excluida.");

        db.Notas.Remove(nota);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================
    // Apoio
    // ==================================================================

    private static void GarantirAberta(NotaFiscal nota)
    {
        if (nota.Status == StatusNota.Aberta) return;

        throw ErroDeNegocioException.Conflito("NOTA_NAO_ESTA_ABERTA",
            $"A nota {nota.Numero} esta {nota.Status} e nao pode mais ser alterada.",
            new { notaId = nota.Id, status = nota.Status.ToString() });
    }

    private static List<ItemNotaRequisicao> Consolidar(IReadOnlyList<ItemNotaRequisicao> itens)
        => itens.GroupBy(i => i.ProdutoId)
                .Select(g => g.First() with { Quantidade = g.Sum(x => x.Quantidade) })
                .OrderBy(i => i.ProdutoId)
                .ToList();

    /// <summary>
    /// Numeracao sequencial sem buraco.
    ///
    /// O UPDATE trava a linha do contador ate o commit da transacao. Duas notas criadas no mesmo
    /// instante entram em fila aqui: a segunda so le o contador depois que a primeira terminou.
    /// E por isso que nao usamos MAX(Numero)+1, que daria numero repetido sob concorrencia.
    /// </summary>
    private async Task<int> ProximoNumeroAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Sequencias SET UltimoValor = UltimoValor + 1 WHERE Nome = 'NotaFiscal'", ct);

        return await db.Sequencias.AsNoTracking()
            .Where(s => s.Nome == "NotaFiscal")
            .Select(s => s.UltimoValor)
            .FirstAsync(ct);
    }

    /// <summary>
    /// Confere os produtos no Estoque, mas SEM tornar a criacao de nota refem dele.
    /// Se o Estoque responde, aproveitamos para barrar produto inexistente cedo.
    /// Se o Estoque esta fora, seguimos: a nota guarda a foto do produto e a conferencia
    /// definitiva acontece na impressao, que e onde o saldo realmente importa.
    /// </summary>
    private async Task ConferirProdutosAsync(List<ItemNotaRequisicao> itens, CancellationToken ct)
    {
        foreach (var item in itens)
        {
            try
            {
                var produto = await estoque.ConsultarProdutoAsync(item.ProdutoId, ct);
                if (produto is null)
                    throw ErroDeNegocioException.Invalido("PRODUTO_INEXISTENTE",
                        $"O produto {item.Codigo} (id {item.ProdutoId}) nao existe no Estoque.",
                        new { produtoId = item.ProdutoId, codigo = item.Codigo });
            }
            catch (Exception ex) when (ex is not ErroDeNegocioException)
            {
                log.LogWarning(ex,
                    "Nao foi possivel conferir o produto {Produto} no Estoque. " +
                    "Seguindo assim mesmo: a conferencia definitiva acontece na impressao.",
                    item.ProdutoId);
                return;
            }
        }
    }
}
