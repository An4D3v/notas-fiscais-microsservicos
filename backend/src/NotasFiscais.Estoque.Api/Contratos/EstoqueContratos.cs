namespace NotasFiscais.Estoque.Api.Contratos;

// ---------- Produtos ----------

public record ProdutoResposta(
    int Id,
    string Codigo,
    string Descricao,
    int Saldo,
    DateTime CriadoEm,
    DateTime AtualizadoEm);

public record CriarProdutoRequisicao(string Codigo, string Descricao, int Saldo);

public record AtualizarProdutoRequisicao(string Descricao, int Saldo);

public record PaginaResposta<T>(IReadOnlyList<T> Itens, int Total, int Pagina, int Tamanho);

// ---------- Baixa / estorno de saldo ----------

public record ItemMovimentoRequisicao(int ProdutoId, int Quantidade);

public record MovimentarEstoqueRequisicao(
    int NotaFiscalId,
    int NotaNumero,
    IReadOnlyList<ItemMovimentoRequisicao> Itens);

public record ItemMovimentoResposta(
    int ProdutoId,
    string Codigo,
    string Descricao,
    int Quantidade,
    int SaldoResultante);

public record MovimentoResposta(
    int MovimentoId,
    string ChaveIdempotencia,
    string Tipo,
    int NotaFiscalId,
    int NotaNumero,
    DateTime OcorridoEm,
    IReadOnlyList<ItemMovimentoResposta> Itens)
{
    /// <summary>
    /// true quando este pedido ja tinha sido processado antes e estamos apenas repetindo a resposta.
    /// Fica de fora do que e gravado em banco: e informacao do transporte, nao do movimento.
    /// </summary>
    public bool Repetido { get; init; }
}
