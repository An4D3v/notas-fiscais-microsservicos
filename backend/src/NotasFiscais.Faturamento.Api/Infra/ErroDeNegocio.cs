using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace NotasFiscais.Faturamento.Api.Infra;

/// <summary>
/// Excecao de REGRA DE NEGOCIO. O sistema funcionou; a operacao e que nao e permitida.
/// </summary>
public class ErroDeNegocioException(string codigo, string mensagem, int statusHttp, object? detalhes = null)
    : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
    public int StatusHttp { get; } = statusHttp;
    public object? Detalhes { get; } = detalhes;

    public static ErroDeNegocioException NaoEncontrado(string mensagem, object? detalhes = null)
        => new("NAO_ENCONTRADO", mensagem, StatusCodes.Status404NotFound, detalhes);

    public static ErroDeNegocioException Invalido(string codigo, string mensagem, object? detalhes = null)
        => new(codigo, mensagem, StatusCodes.Status400BadRequest, detalhes);

    public static ErroDeNegocioException Conflito(string codigo, string mensagem, object? detalhes = null)
        => new(codigo, mensagem, StatusCodes.Status409Conflict, detalhes);

    /// <summary>503: o Estoque nao respondeu. Nao e culpa do usuario, e nao e bug: e indisponibilidade.</summary>
    public static ErroDeNegocioException Indisponivel(string mensagem, object? detalhes = null)
        => new("ESTOQUE_INDISPONIVEL", mensagem, StatusCodes.Status503ServiceUnavailable, detalhes);
}

public class TratadorGlobalDeErros(ILogger<TratadorGlobalDeErros> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, codigo, titulo, detalhes) = ex switch
        {
            ErroDeNegocioException n => (n.StatusHttp, n.Codigo, n.Message, n.Detalhes),

            BadHttpRequestException b => (StatusCodes.Status400BadRequest, "REQUISICAO_INVALIDA",
                                          b.Message, (object?)null),

            OperationCanceledException => (499, "REQUISICAO_CANCELADA", "A requisicao foi cancelada.", null),

            _ => (StatusCodes.Status500InternalServerError, "ERRO_INTERNO",
                  "Erro inesperado ao processar a requisicao.", (object?)null)
        };

        // Um erro PREVISTO (inclusive 503 de dependencia fora do ar) e aviso, nao falha do sistema:
        // sai sem stack trace, para nao poluir o log com ruido que parece bug.
        // Stack trace fica reservado ao que a gente realmente nao esperava.
        if (ex is ErroDeNegocioException)
            log.LogWarning("{Codigo} em {Metodo} {Rota}: {Mensagem}",
                codigo, ctx.Request.Method, ctx.Request.Path, ex.Message);
        else if (status >= 500)
            log.LogError(ex, "Falha nao tratada em {Metodo} {Rota}", ctx.Request.Method, ctx.Request.Path);
        else
            log.LogWarning("{Codigo} em {Metodo} {Rota}: {Mensagem}",
                codigo, ctx.Request.Method, ctx.Request.Path, ex.Message);

        var problema = new ProblemDetails
        {
            Status = status,
            Title = titulo,
            Type = $"https://notas-fiscais.local/erros/{codigo.ToLowerInvariant()}",
            Instance = $"{ctx.Request.Method} {ctx.Request.Path}"
        };
        problema.Extensions["codigo"] = codigo;
        problema.Extensions["traceId"] = ctx.TraceIdentifier;
        if (detalhes is not null) problema.Extensions["detalhes"] = detalhes;

        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(problema, ct);
        return true;
    }
}

/// <summary>Roda o validador do FluentValidation antes do handler do endpoint.</summary>
public class ValidacaoEndpointFilter<T>(IValidator<T> validador) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var alvo = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (alvo is null) return await next(ctx);

        var resultado = await validador.ValidateAsync(alvo, ctx.HttpContext.RequestAborted);
        if (resultado.IsValid) return await next(ctx);

        throw ErroDeNegocioException.Invalido(
            "DADOS_INVALIDOS",
            "Os dados enviados nao passaram na validacao.",
            resultado.Errors.Select(e => new { campo = e.PropertyName, erro = e.ErrorMessage }));
    }
}
