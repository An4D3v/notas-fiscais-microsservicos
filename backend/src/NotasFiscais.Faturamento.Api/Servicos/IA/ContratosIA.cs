namespace NotasFiscais.Faturamento.Api.Servicos.IA;

/// <summary>Texto livre digitado pela pessoa: "3 canetas azuis e 2 cadernos".</summary>
public record InterpretarNotaRequisicao(string Texto);

/// <summary>Um item que a interpretacao sugeriu, ja casado com um produto REAL do catalogo.</summary>
public record ItemSugerido(
    int ProdutoId,
    string Codigo,
    string Descricao,
    int Quantidade,
    int SaldoAtual,
    double Confianca,
    string Trecho)
{
    /// <summary>true quando a quantidade pedida ja passa do saldo que existe hoje.</summary>
    public bool AcimaDoSaldo => Quantidade > SaldoAtual;
}

public record TrechoNaoEntendido(string Trecho, string Motivo);

// Modo: "ia" quando veio do modelo de linguagem, "offline" quando foi o casamento
// local por semelhanca de texto. A tela mostra isso, para ninguem achar que tem IA
// rodando quando nao tem.
public record InterpretacaoResposta(
    string Modo,
    string? Modelo,
    IReadOnlyList<ItemSugerido> Itens,
    IReadOnlyList<TrechoNaoEntendido> NaoEntendidos,
    string? Aviso);

/// <summary>Saida crua da interpretacao, ANTES de ser conferida contra o catalogo.</summary>
public record ItemCru(string Codigo, int Quantidade, string Trecho, double Confianca);

public record InterpretacaoCrua(
    string Modo,
    string? Modelo,
    IReadOnlyList<ItemCru> Itens,
    IReadOnlyList<TrechoNaoEntendido> NaoEntendidos);

/// <summary>Produto do catalogo, como o interpretador precisa ver.</summary>
public record ProdutoDoCatalogo(int Id, string Codigo, string Descricao, int Saldo);

/// <summary>
/// Quem sabe transformar texto livre em itens de nota.
/// Duas implementacoes: uma chama o modelo de linguagem, a outra casa por semelhanca
/// de texto aqui mesmo. A segunda existe para o sistema NUNCA depender de chave de API.
/// </summary>
public interface IInterpretadorDeNota
{
    // O modo usado vem DENTRO do resultado, e nao numa propriedade do servico.
    // Guardar "qual foi o modo da ultima chamada" no objeto daria resposta trocada
    // se duas pessoas usassem a funcionalidade ao mesmo tempo.
    Task<InterpretacaoCrua> InterpretarAsync(
        string texto, IReadOnlyList<ProdutoDoCatalogo> catalogo, CancellationToken ct);
}
