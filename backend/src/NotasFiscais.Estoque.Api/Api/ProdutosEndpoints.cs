using NotasFiscais.Estoque.Api.Contratos;
using NotasFiscais.Estoque.Api.Dados;
using NotasFiscais.Estoque.Api.Dominio;
using NotasFiscais.Estoque.Api.Infra;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Estoque.Api.Api;

public static class ProdutosEndpoints
{
    public static void MapProdutos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/produtos").WithTags("Produtos");

        // ------------------------------------------------------------------
        // Listagem com busca e paginacao.
        // Tudo em LINQ: o Where/OrderBy/Skip/Take vira SQL, nada e trazido para
        // a memoria antes de filtrar, e o Select projeta direto no DTO
        // (nao carrega a entidade inteira nem a coluna Versao).
        // ------------------------------------------------------------------
        grupo.MapGet("/", async (
            EstoqueDbContext db,
            string? busca,
            int? pagina,
            int? tamanho,
            CancellationToken ct) =>
        {
            // Anulaveis de proposito: GET /produtos sozinho tem que funcionar.
            // (Em Minimal API, um int nao-anulavel na query vira parametro OBRIGATORIO.)
            var nPagina = pagina is null or <= 0 ? 1 : pagina.Value;
            var nTamanho = tamanho is null or <= 0 or > 200 ? 20 : tamanho.Value;

            var consulta = db.Produtos.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                consulta = consulta.Where(p =>
                    EF.Functions.Like(p.Codigo, $"%{termo}%") ||
                    EF.Functions.Like(p.Descricao, $"%{termo}%"));
            }

            var total = await consulta.CountAsync(ct);

            var itens = await consulta
                .OrderBy(p => p.Codigo)
                .Skip((nPagina - 1) * nTamanho)
                .Take(nTamanho)
                .Select(p => new ProdutoResposta(
                    p.Id, p.Codigo, p.Descricao, p.Saldo, p.CriadoEm, p.AtualizadoEm))
                .ToListAsync(ct);

            return Results.Ok(new PaginaResposta<ProdutoResposta>(itens, total, nPagina, nTamanho));
        })
        .WithSummary("Lista produtos com busca por codigo ou descricao.");

        // ------------------------------------------------------------------
        grupo.MapGet("/{id:int}", async (int id, EstoqueDbContext db, CancellationToken ct) =>
        {
            var p = await db.Produtos.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new ProdutoResposta(x.Id, x.Codigo, x.Descricao, x.Saldo, x.CriadoEm, x.AtualizadoEm))
                .FirstOrDefaultAsync(ct);

            return p is null
                ? throw ErroDeNegocioException.NaoEncontrado($"Produto {id} nao encontrado.")
                : Results.Ok(p);
        })
        .WithSummary("Busca um produto pelo id.");

        // ------------------------------------------------------------------
        grupo.MapPost("/", async (CriarProdutoRequisicao req, EstoqueDbContext db, CancellationToken ct) =>
        {
            var codigo = req.Codigo.Trim().ToUpperInvariant();

            if (await db.Produtos.AnyAsync(p => p.Codigo == codigo, ct))
                throw ErroDeNegocioException.Conflito("CODIGO_JA_EXISTE",
                    $"Ja existe um produto com o codigo {codigo}.", new { codigo });

            var produto = new Produto
            {
                Codigo = codigo,
                Descricao = req.Descricao.Trim(),
                Saldo = req.Saldo,
                CriadoEm = DateTime.UtcNow,
                AtualizadoEm = DateTime.UtcNow
            };

            db.Produtos.Add(produto);
            await db.SaveChangesAsync(ct);

            var resposta = new ProdutoResposta(produto.Id, produto.Codigo, produto.Descricao,
                produto.Saldo, produto.CriadoEm, produto.AtualizadoEm);

            return Results.Created($"/produtos/{produto.Id}", resposta);
        })
        .AddEndpointFilter<ValidacaoEndpointFilter<CriarProdutoRequisicao>>()
        .WithSummary("Cadastra um produto.");

        // ------------------------------------------------------------------
        // Edicao: aqui SIM usamos concorrencia otimista do EF (coluna Versao).
        // Se duas telas editarem o mesmo produto, a segunda leva 409 em vez de
        // sobrescrever a alteracao da primeira sem ninguem perceber.
        // ------------------------------------------------------------------
        grupo.MapPut("/{id:int}", async (
            int id, AtualizarProdutoRequisicao req, EstoqueDbContext db, CancellationToken ct) =>
        {
            var produto = await db.Produtos.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw ErroDeNegocioException.NaoEncontrado($"Produto {id} nao encontrado.");

            produto.Descricao = req.Descricao.Trim();
            produto.Saldo = req.Saldo;
            produto.AtualizadoEm = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw ErroDeNegocioException.Conflito("PRODUTO_ALTERADO_POR_OUTRO",
                    "Este produto foi alterado por outra pessoa enquanto voce editava. Recarregue e tente de novo.",
                    new { produtoId = id });
            }

            return Results.Ok(new ProdutoResposta(produto.Id, produto.Codigo, produto.Descricao,
                produto.Saldo, produto.CriadoEm, produto.AtualizadoEm));
        })
        .AddEndpointFilter<ValidacaoEndpointFilter<AtualizarProdutoRequisicao>>()
        .WithSummary("Atualiza descricao e saldo de um produto.");

        // ------------------------------------------------------------------
        grupo.MapDelete("/{id:int}", async (int id, EstoqueDbContext db, CancellationToken ct) =>
        {
            var produto = await db.Produtos.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw ErroDeNegocioException.NaoEncontrado($"Produto {id} nao encontrado.");

            var temMovimento = await db.MovimentoItens.AnyAsync(i => i.ProdutoId == id, ct);
            if (temMovimento)
                throw ErroDeNegocioException.Conflito("PRODUTO_COM_MOVIMENTO",
                    "Produto ja utilizado em nota impressa, nao pode ser excluido.", new { produtoId = id });

            db.Produtos.Remove(produto);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Remove um produto que nunca foi movimentado.");
    }
}
