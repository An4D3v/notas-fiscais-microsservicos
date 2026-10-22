using FluentValidation;
using NotasFiscais.Estoque.Api.Contratos;

namespace NotasFiscais.Estoque.Api.Validacoes;

public class CriarProdutoValidador : AbstractValidator<CriarProdutoRequisicao>
{
    public CriarProdutoValidador()
    {
        RuleFor(x => x.Codigo)
            .NotEmpty().WithMessage("O codigo do produto e obrigatorio.")
            .MaximumLength(30).WithMessage("O codigo deve ter no maximo 30 caracteres.");

        RuleFor(x => x.Descricao)
            .NotEmpty().WithMessage("A descricao e obrigatoria.")
            .MaximumLength(200).WithMessage("A descricao deve ter no maximo 200 caracteres.");

        RuleFor(x => x.Saldo)
            .GreaterThanOrEqualTo(0).WithMessage("O saldo inicial nao pode ser negativo.");
    }
}

public class AtualizarProdutoValidador : AbstractValidator<AtualizarProdutoRequisicao>
{
    public AtualizarProdutoValidador()
    {
        RuleFor(x => x.Descricao)
            .NotEmpty().WithMessage("A descricao e obrigatoria.")
            .MaximumLength(200).WithMessage("A descricao deve ter no maximo 200 caracteres.");

        RuleFor(x => x.Saldo)
            .GreaterThanOrEqualTo(0).WithMessage("O saldo nao pode ser negativo.");
    }
}
