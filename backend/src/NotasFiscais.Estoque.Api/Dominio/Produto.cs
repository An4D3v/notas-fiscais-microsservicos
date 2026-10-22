namespace NotasFiscais.Estoque.Api.Dominio;

/// <summary>
/// Produto do almoxarifado. Este servico e o unico dono do campo <see cref="Saldo"/>:
/// nenhum outro servico escreve nesta tabela.
/// </summary>
public class Produto
{
    public int Id { get; set; }

    /// <summary>Codigo de negocio, unico (ex.: P-001).</summary>
    public string Codigo { get; set; } = string.Empty;

    public string Descricao { get; set; } = string.Empty;

    /// <summary>Quantidade disponivel. Nunca pode ficar negativa (garantido por CHECK no banco).</summary>
    public int Saldo { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>
    /// Carimbo de versao do SQL Server. O EF usa para concorrencia otimista na EDICAO do produto:
    /// se duas telas editarem o mesmo produto, a segunda recebe erro em vez de sobrescrever calado.
    /// (A baixa de saldo NAO usa este caminho: ela usa UPDATE atomico, ver EstoqueServico.)
    /// </summary>
    public byte[]? Versao { get; set; }
}
