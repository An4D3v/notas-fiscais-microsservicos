namespace NotasFiscais.Faturamento.Api.Contratos;

// ---------- Entrada ----------

public record ItemNotaRequisicao(int ProdutoId, string Codigo, string Descricao, int Quantidade);

public record CriarNotaRequisicao(IReadOnlyList<ItemNotaRequisicao> Itens);

public record AdicionarItensRequisicao(IReadOnlyList<ItemNotaRequisicao> Itens);

// ---------- Saida ----------

public record ItemNotaResposta(
    int Id, int ProdutoId, string Codigo, string Descricao, int Quantidade);

public record NotaResposta(
    int Id,
    int Numero,
    string Status,
    DateTime CriadaEm,
    DateTime? ImpressaEm,
    string? UltimoErro,
    IReadOnlyList<ItemNotaResposta> Itens);

public record SaldoAtualizado(
    int ProdutoId, string Codigo, string Descricao, int Quantidade, int SaldoResultante);

public record ImpressaoResposta(
    NotaResposta Nota,
    IReadOnlyList<SaldoAtualizado> SaldosAtualizados,
    bool Repetido);

public record PaginaResposta<T>(IReadOnlyList<T> Itens, int Total, int Pagina, int Tamanho);
