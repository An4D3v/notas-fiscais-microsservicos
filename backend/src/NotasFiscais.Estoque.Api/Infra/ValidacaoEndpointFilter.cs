using FluentValidation;

namespace NotasFiscais.Estoque.Api.Infra;

/// <summary>
/// Filtro de endpoint (recurso do Minimal API) que roda o validador do FluentValidation
/// antes do handler. Evita repetir "if (string.IsNullOrWhiteSpace(...))" em todo endpoint
/// e devolve todos os erros de uma vez, em vez de um por vez.
/// </summary>
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
