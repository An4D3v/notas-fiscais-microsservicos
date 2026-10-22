using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace NotasFiscais.Estoque.Api.Infra;

/// <summary>
/// Excecao de REGRA DE NEGOCIO (saldo insuficiente, produto inexistente, codigo repetido...).
/// Diferente de excecao tecnica: aqui o sistema funcionou, a operacao e que nao e permitida.
/// Sempre carrega um <see cref="Codigo"/> estavel, que a tela usa para decidir a mensagem.
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
}

/// <summary>
/// Traduz QUALQUER excecao que escape dos endpoints para o formato ProblemDetails (RFC 7807),
/// que e o padrao da web para corpo de erro. Assim a tela tem sempre a mesma forma para ler,
/// e nenhum stack trace vaza para o usuario final.
/// </summary>
public class TratadorGlobalDeErros(ILogger<TratadorGlobalDeErros> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, codigo, titulo, detalhes) = ex switch
        {
            ErroDeNegocioException n => (n.StatusHttp, n.Codigo, n.Message, n.Detalhes),

            // Requisicao malformada (parametro faltando, JSON invalido, tipo errado).
            // Sem este caso o ASP.NET deixaria virar 500, culpando o servidor por erro do cliente.
            BadHttpRequestException b => (StatusCodes.Status400BadRequest, "REQUISICAO_INVALIDA",
                                          b.Message, (object?)null),

            OperationCanceledException => (499, "REQUISICAO_CANCELADA", "A requisicao foi cancelada.", null),
            _ => (StatusCodes.Status500InternalServerError, "ERRO_INTERNO",
                  "Erro inesperado ao processar a requisicao.", (object?)null)
        };

        // Erro PREVISTO sai sem stack trace, para nao poluir o log com ruido que parece bug.
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

/// <summary>
/// Interruptor de caos: permite derrubar o servico "de mentira" pela API, sem matar o processo.
/// Serve para os testes automatizados e para gravar o video sem precisar fechar terminal.
/// Fora de ambiente de desenvolvimento este endpoint nem e registrado.
/// </summary>
public class ModoCaos
{
    public bool Ativo { get; set; }
    public int AtrasoMs { get; set; }
}
