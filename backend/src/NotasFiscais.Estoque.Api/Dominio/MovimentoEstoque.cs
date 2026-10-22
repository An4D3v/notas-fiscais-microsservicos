namespace NotasFiscais.Estoque.Api.Dominio;

public enum TipoMovimento
{
    Baixa = 1,
    Estorno = 2
}

/// <summary>
/// Livro-razao do estoque. Cada baixa ou estorno vira uma linha aqui.
///
/// Este e o mecanismo de IDEMPOTENCIA: a coluna <see cref="ChaveIdempotencia"/> tem indice UNICO.
/// Se o mesmo pedido chegar duas vezes (duplo clique, retry automatico), a segunda tentativa
/// esbarra no indice e devolvemos a resposta que ja foi dada, sem mexer no saldo de novo.
/// </summary>
public class MovimentoEstoque
{
    public int Id { get; set; }

    /// <summary>Chave que o chamador envia no header Idempotency-Key. Indice UNICO.</summary>
    public string ChaveIdempotencia { get; set; } = string.Empty;

    public TipoMovimento Tipo { get; set; }

    /// <summary>Referencia à nota que originou o movimento (rastreabilidade entre os dois servicos).</summary>
    public int NotaFiscalId { get; set; }
    public int NotaNumero { get; set; }

    public DateTime OcorridoEm { get; set; }

    /// <summary>
    /// Resposta devolvida na primeira vez, em JSON. Repetir o pedido devolve exatamente isto.
    /// Guardar a RESPOSTA (e nao so a chave) e o que faz a idempotencia ser util para a tela:
    /// o segundo clique recebe os mesmos saldos, em vez de um 200 vazio.
    /// </summary>
    public string RespostaJson { get; set; } = string.Empty;

    public List<MovimentoItem> Itens { get; set; } = new();
}

public class MovimentoItem
{
    public int Id { get; set; }
    public int MovimentoEstoqueId { get; set; }
    public int ProdutoId { get; set; }
    public string ProdutoCodigo { get; set; } = string.Empty;
    public int Quantidade { get; set; }

    /// <summary>Saldo que o produto ficou depois deste movimento (foto do momento).</summary>
    public int SaldoResultante { get; set; }

    public MovimentoEstoque? Movimento { get; set; }
}
