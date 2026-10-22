using FluentValidation;
using NotasFiscais.Faturamento.Api.Contratos;

namespace NotasFiscais.Faturamento.Api.Validacoes;

public class ItemNotaValidador : AbstractValidator<ItemNotaRequisicao>
{
    public ItemNotaValidador()
    {
        RuleFor(x => x.ProdutoId).GreaterThan(0).WithMessage("Informe o produto.");
        RuleFor(x => x.Codigo).NotEmpty().WithMessage("O codigo do produto e obrigatorio.");
        RuleFor(x => x.Descricao).NotEmpty().WithMessage("A descricao do produto e obrigatoria.");
        RuleFor(x => x.Quantidade).GreaterThan(0).WithMessage("A quantidade deve ser maior que zero.");
    }
}

public class CriarNotaValidador : AbstractValidator<CriarNotaRequisicao>
{
    public CriarNotaValidador()
    {
        RuleFor(x => x.Itens)
            .NotEmpty().WithMessage("A nota precisa de pelo menos um produto.");

        RuleForEach(x => x.Itens).SetValidator(new ItemNotaValidador());
    }
}

public class AdicionarItensValidador : AbstractValidator<AdicionarItensRequisicao>
{
    public AdicionarItensValidador()
    {
        RuleFor(x => x.Itens)
            .NotEmpty().WithMessage("Informe ao menos um produto para adicionar.");

        RuleForEach(x => x.Itens).SetValidator(new ItemNotaValidador());
    }
}
