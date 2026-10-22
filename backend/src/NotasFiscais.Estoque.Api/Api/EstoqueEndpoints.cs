using NotasFiscais.Estoque.Api.Contratos;
using NotasFiscais.Estoque.Api.Infra;
using NotasFiscais.Estoque.Api.Servicos;

namespace NotasFiscais.Estoque.Api.Api;

public static class EstoqueEndpoints
{
    /// <summary>
    /// Le o header Idempotency-Key. E obrigatorio: sem ele nao ha como garantir
    /// que um retry nao baixe o estoque duas vezes.
    /// </summary>
    private static string ChaveDe(HttpContext ctx)
    {
        var chave = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(chave))
            throw ErroDeNegocioException.Invalido("CHAVE_IDEMPOTENCIA_OBRIGATORIA",
                "Envie o header Idempotency-Key nesta operacao.");
        return chave.Trim();
    }

    public static void MapEstoque(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/estoque").WithTags("Estoque");

        // ------------------------------------------------------------------
        // Baixa de saldo. Chamado pelo servico de Faturamento na impressao da nota.
        // ------------------------------------------------------------------
        grupo.MapPost("/baixas", async (
            MovimentarEstoqueRequisicao req,
            HttpContext ctx,
            EstoqueServico servico,
            CancellationToken ct) =>
        {
            var resultado = await servico.BaixarAsync(ChaveDe(ctx), req, ct);
            return Results.Ok(resultado);
        })
        .WithSummary("Baixa saldo dos produtos de uma nota (idempotente pelo header Idempotency-Key).");

        // ------------------------------------------------------------------
        // Estorno: devolve o saldo. E a COMPENSACAO quando a nota nao consegue fechar
        // depois de o saldo ja ter sido baixado.
        // ------------------------------------------------------------------
        grupo.MapPost("/estornos", async (
            MovimentarEstoqueRequisicao req,
            HttpContext ctx,
            EstoqueServico servico,
            CancellationToken ct) =>
        {
            var resultado = await servico.EstornarAsync(ChaveDe(ctx), req, ct);
            return Results.Ok(resultado);
        })
        .WithSummary("Devolve saldo ao estoque (compensacao).");

        // ------------------------------------------------------------------
        // "Essa baixa chegou a acontecer?"
        // O Faturamento pergunta isso quando perde a resposta no meio do caminho.
        // ------------------------------------------------------------------
        grupo.MapGet("/movimentos/{chave}", async (
            string chave, EstoqueServico servico, CancellationToken ct) =>
        {
            var mov = await servico.BuscarPorChaveAsync(chave, ct);
            return mov is null ? Results.NotFound() : Results.Ok(mov);
        })
        .WithSummary("Consulta um movimento pela chave de idempotencia.");
    }

    /// <summary>
    /// Endpoints de teste de falha. So sao registrados fora de producao.
    /// Permitem "derrubar" o servico pela API, sem fechar o processo.
    /// </summary>
    public static void MapCaos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/admin/caos").WithTags("Caos (somente desenvolvimento)");

        grupo.MapGet("/", (ModoCaos caos) => Results.Ok(new { caos.Ativo, caos.AtrasoMs }));

        grupo.MapPost("/", (bool ativo, int? atrasoMs, ModoCaos caos, ILogger<ModoCaos> log) =>
        {
            caos.Ativo = ativo;
            caos.AtrasoMs = atrasoMs ?? 0;
            log.LogWarning("MODO CAOS alterado: ativo={Ativo}, atraso={Atraso}ms", caos.Ativo, caos.AtrasoMs);
            return Results.Ok(new { caos.Ativo, caos.AtrasoMs });
        })
        .WithSummary("Liga/desliga a simulacao de indisponibilidade do servico.");
    }
}
