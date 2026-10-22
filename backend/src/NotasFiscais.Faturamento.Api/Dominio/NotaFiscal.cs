namespace NotasFiscais.Faturamento.Api.Dominio;

public enum StatusNota
{
    /// <summary>Pode receber itens e pode ser impressa.</summary>
    Aberta = 1,

    /// <summary>
    /// Estado intermediario: pedimos a baixa ao Estoque e ainda nao sabemos o desfecho.
    /// Existe justamente para que uma queda no meio do caminho nao deixe a nota num limbo silencioso.
    /// </summary>
    Processando = 2,

    /// <summary>Impressa. Saldo ja baixado. Nao pode ser impressa de novo.</summary>
    Fechada = 3
}

public class NotaFiscal
{
    public int Id { get; set; }

    /// <summary>Numeracao sequencial, sem buraco, gerada pela tabela Sequencias.</summary>
    public int Numero { get; set; }

    public StatusNota Status { get; set; } = StatusNota.Aberta;

    public DateTime CriadaEm { get; set; }
    public DateTime AtualizadaEm { get; set; }
    public DateTime? ImpressaEm { get; set; }

    /// <summary>
    /// Chave de idempotencia da impressao. Guardada na nota para dois fins:
    /// repetir a mesma resposta se o mesmo pedido voltar, e perguntar ao Estoque
    /// "essa baixa aconteceu?" quando perdemos a resposta.
    /// </summary>
    public string? ChaveImpressao { get; set; }

    /// <summary>Ultimo erro conhecido da tentativa de impressao (mostrado na tela).</summary>
    public string? UltimoErro { get; set; }

    public byte[]? Versao { get; set; }

    public List<ItemNota> Itens { get; set; } = new();
}

/// <summary>
/// Item da nota. Guarda uma FOTO do produto (codigo e descricao no momento da emissao),
/// e nao apenas o id. Isso e correto em nota fiscal: se o produto for renomeado amanha,
/// a nota emitida hoje continua dizendo o que foi vendido hoje.
/// Tambem e o que deixa o Faturamento criar nota sem depender do Estoque estar no ar.
/// </summary>
public class ItemNota
{
    public int Id { get; set; }
    public int NotaFiscalId { get; set; }
    public int ProdutoId { get; set; }
    public string ProdutoCodigo { get; set; } = string.Empty;
    public string ProdutoDescricao { get; set; } = string.Empty;
    public int Quantidade { get; set; }

    public NotaFiscal? Nota { get; set; }
}

/// <summary>
/// Contador de numeracao. Uma linha por sequencia.
/// O incremento e feito com UPDATE dentro de transacao: o proximo pedido espera
/// o commit do anterior, entao dois usuarios simultaneos nunca recebem o mesmo numero.
/// </summary>
public class Sequencia
{
    public string Nome { get; set; } = string.Empty;
    public int UltimoValor { get; set; }
}
