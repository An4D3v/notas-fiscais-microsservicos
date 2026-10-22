using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NotasFiscais.Faturamento.Api.Servicos.IA;

/// <summary>
/// Interpretacao SEM inteligencia artificial nenhuma: quebra o texto em pedacos do tipo
/// "3 canetas azuis" e casa cada pedaco com o produto mais parecido do catalogo,
/// contando palavras em comum.
///
/// Por que isto existe: quem clonar este repositorio normalmente nao tem
/// chave de API. Se a tela quebrasse nesse caso, a funcionalidade de IA valeria zero.
/// Aqui ela degrada: funciona pior, avisa que esta em modo offline, e nunca quebra.
/// </summary>
public partial class InterpretadorOffline : IInterpretadorDeNota
{
    // Palavras que nao ajudam a identificar produto e so atrapalham a pontuacao.
    private static readonly HashSet<string> Ruido =
    [
        "de", "da", "do", "das", "dos", "e", "com", "para", "por", "um", "uma", "uns", "umas",
        "o", "a", "os", "as", "no", "na", "nos", "nas", "unidade", "unidades", "und", "un",
        "item", "itens", "produto", "produtos", "nota", "fiscal", "quero", "coloca", "coloque",
        "adiciona", "adicione", "faz", "faca", "fazer", "monta", "monte", "criar", "cria"
    ];

    private const double PontuacaoMinima = 0.34;

    public Task<InterpretacaoCrua> InterpretarAsync(
        string texto, IReadOnlyList<ProdutoDoCatalogo> catalogo, CancellationToken ct)
    {
        var itens = new List<ItemCru>();
        var naoEntendidos = new List<TrechoNaoEntendido>();

        foreach (var pedaco in QuebrarEmPedacos(texto))
        {
            var melhor = MelhorCandidato(pedaco.Descricao, catalogo);

            if (melhor is null)
            {
                naoEntendidos.Add(new TrechoNaoEntendido(pedaco.Trecho,
                    "Nao encontrei nenhum produto cadastrado parecido com isso."));
                continue;
            }

            itens.Add(new ItemCru(melhor.Value.produto.Codigo, pedaco.Quantidade,
                pedaco.Trecho, Math.Round(melhor.Value.pontos, 2)));
        }

        return Task.FromResult(new InterpretacaoCrua("offline", null, itens, naoEntendidos));
    }

    // ==================================================================
    // Quebra o texto em (quantidade, descricao)
    // ==================================================================

    private record Pedaco(int Quantidade, string Descricao, string Trecho);

    private static IEnumerable<Pedaco> QuebrarEmPedacos(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) yield break;

        // Separa por virgula, ponto e virgula, quebra de linha ou a palavra "e".
        var partes = SeparadorDeItens().Split(texto);

        foreach (var parte in partes)
        {
            var limpo = parte.Trim();
            if (limpo.Length == 0) continue;

            // A quantidade pode estar em qualquer lugar do pedaco, e nao so no comeco:
            // "quero 4 grampeadores" tem que dar 4, e nao 1.
            var casamento = PrimeiraQuantidade().Match(limpo);

            // Sem numero nenhum, assume 1 ("uma caneta azul", "caderno").
            var quantidade = 1;
            var descricao = limpo;

            if (casamento.Success)
            {
                quantidade = int.Parse(casamento.Groups["qtd"].Value, CultureInfo.InvariantCulture);

                // Tira so o numero (e a unidade, se veio junto), preservando o resto da frase.
                descricao = (limpo[..casamento.Index] + " " + limpo[(casamento.Index + casamento.Length)..])
                    .Trim();
            }

            if (descricao.Length == 0) continue;
            if (quantidade is <= 0 or > 100000) continue;

            yield return new Pedaco(quantidade, descricao, limpo);
        }
    }

    // ==================================================================
    // Casamento por semelhanca
    // ==================================================================

    private static (ProdutoDoCatalogo produto, double pontos)? MelhorCandidato(
        string descricao, IReadOnlyList<ProdutoDoCatalogo> catalogo)
    {
        var palavrasBusca = Tokenizar(descricao);
        if (palavrasBusca.Count == 0) return null;

        (ProdutoDoCatalogo produto, double pontos)? melhor = null;

        foreach (var produto in catalogo)
        {
            // O codigo escrito no texto vale por si so ("2 de P-001").
            if (Normalizar(descricao).Contains(Normalizar(produto.Codigo)))
                return (produto, 1.0);

            var palavrasProduto = Tokenizar(produto.Descricao);
            if (palavrasProduto.Count == 0) continue;

            var acertos = palavrasBusca.Count(b => palavrasProduto.Any(p => Combinam(b, p)));
            if (acertos == 0) continue;

            // Proporcao das palavras da busca que foram encontradas, com um empurrao
            // pequeno para o produto de descricao mais curta (menos generico).
            var pontos = (double)acertos / palavrasBusca.Count;
            pontos += 0.01 * Math.Max(0, 6 - palavrasProduto.Count);

            if (melhor is null || pontos > melhor.Value.pontos)
                melhor = (produto, Math.Min(pontos, 0.99));
        }

        return melhor is not null && melhor.Value.pontos >= PontuacaoMinima ? melhor : null;
    }

    /// <summary>Compara duas palavras tolerando plural e variacao de final ("caneta"/"canetas").</summary>
    private static bool Combinam(string a, string b)
    {
        if (a == b) return true;
        if (a.Length >= 4 && b.StartsWith(a, StringComparison.Ordinal)) return true;
        if (b.Length >= 4 && a.StartsWith(b, StringComparison.Ordinal)) return true;
        return false;
    }

    private static List<string> Tokenizar(string texto) =>
        Normalizar(texto)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 1 && !Ruido.Contains(p))
            .Distinct()
            .ToList();

    /// <summary>Minusculas, sem acento e sem pontuacao, para "Caneta Azul" casar com "caneta azul".</summary>
    private static string Normalizar(string texto)
    {
        var decomposto = texto.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var construtor = new StringBuilder(decomposto.Length);

        foreach (var caractere in decomposto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(caractere);
            if (categoria == UnicodeCategory.NonSpacingMark) continue;

            construtor.Append(char.IsLetterOrDigit(caractere) || caractere == '-' ? caractere : ' ');
        }

        return construtor.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s*(?:,|;|\r?\n|\s+e\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SeparadorDeItens();

    /// <summary>
    /// Primeiro numero SOLTO do pedaco, com a unidade opcional grudada ("4", "4x", "4 un").
    ///
    /// Os dois cuidados do padrao:
    ///   (?&lt;![\w-]) e (?![\w-]) impedem pegar o numero de dentro de um codigo:
    ///   em "P-001" o 001 nao vale como quantidade.
    ///   O \b depois da unidade impede "5 unicornios" virar "5 un" + "icornios".
    /// </summary>
    [GeneratedRegex(@"(?<![\w-])(?<qtd>\d{1,6})(?![\w-])\s*(?:(?:x|un|und|unid|unidades?)\b)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex PrimeiraQuantidade();
}
