using NotasFiscais.Faturamento.Api.Contratos;
using NotasFiscais.Faturamento.Api.Infra;
using NotasFiscais.Faturamento.Api.Servicos;
using NotasFiscais.Faturamento.Api.Servicos.IA;

namespace NotasFiscais.Faturamento.Api.Api;

public static class NotasEndpoints
{
    public static void MapNotas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/notas").WithTags("Notas fiscais");

        grupo.MapGet("/", async (string? status, int? pagina, int? tamanho,
            NotaFiscalServico servico, CancellationToken ct) =>
            Results.Ok(await servico.ListarAsync(status, pagina, tamanho, ct)))
        .WithSummary("Lista notas, opcionalmente filtrando por status (Aberta, Processando, Fechada).");

        grupo.MapGet("/{id:int}", async (int id, NotaFiscalServico servico, CancellationToken ct) =>
            Results.Ok(NotaFiscalServico.Mapear(await servico.CarregarAsync(id, ct, rastrear: false))))
        .WithSummary("Busca uma nota pelo id.");

        // ------------------------------------------------------------------
        // Montagem por texto livre. NAO cria nota: devolve uma SUGESTAO de itens
        // para a pessoa conferir e confirmar na tela. A IA propoe, o humano decide.
        // ------------------------------------------------------------------
        grupo.MapPost("/interpretar", async (InterpretarNotaRequisicao req,
            MontadorDeNotaServico montador, CancellationToken ct) =>
            Results.Ok(await montador.InterpretarAsync(req.Texto, ct)))
        .WithSummary("Transforma um pedido escrito em portugues numa sugestao de itens de nota.");

        grupo.MapPost("/", async (CriarNotaRequisicao req, NotaFiscalServico servico, CancellationToken ct) =>
        {
            var nota = await servico.CriarAsync(req, ct);
            return Results.Created($"/notas/{nota.Id}", nota);
        })
        .AddEndpointFilter<ValidacaoEndpointFilter<CriarNotaRequisicao>>()
        .WithSummary("Cria uma nota com numeracao sequencial e status Aberta.");

        grupo.MapPost("/{id:int}/itens", async (int id, AdicionarItensRequisicao req,
            NotaFiscalServico servico, CancellationToken ct) =>
            Results.Ok(await servico.AdicionarItensAsync(id, req, ct)))
        .AddEndpointFilter<ValidacaoEndpointFilter<AdicionarItensRequisicao>>()
        .WithSummary("Adiciona produtos a uma nota aberta.");

        grupo.MapDelete("/{id:int}/itens/{itemId:int}", async (int id, int itemId,
            NotaFiscalServico servico, CancellationToken ct) =>
            Results.Ok(await servico.RemoverItemAsync(id, itemId, ct)))
        .WithSummary("Remove um produto de uma nota aberta.");

        grupo.MapDelete("/{id:int}", async (int id, NotaFiscalServico servico, CancellationToken ct) =>
        {
            await servico.ExcluirAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Exclui uma nota que ainda nao foi impressa.");

        // ------------------------------------------------------------------
        // A IMPRESSAO. O header Idempotency-Key e opcional na borda: se a tela nao
        // mandar, o servidor gera um. Mas a tela DEVE mandar sempre o mesmo valor
        // enquanto tentar a mesma impressao, senao a protecao contra duplo clique nao existe.
        // ------------------------------------------------------------------
        grupo.MapPost("/{id:int}/imprimir", async (int id, HttpContext ctx,
            ImpressaoServico impressao, CancellationToken ct) =>
        {
            var chave = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(chave))
                chave = $"nota-{id}-{Guid.NewGuid()}";

            var resultado = await impressao.ImprimirAsync(id, chave.Trim(), ct);
            return Results.Ok(resultado);
        })
        .WithSummary("Imprime a nota: baixa o saldo no Estoque e fecha a nota. Idempotente.");
    }
}
