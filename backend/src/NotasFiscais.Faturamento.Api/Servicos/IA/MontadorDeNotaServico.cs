using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Infra;

namespace NotasFiscais.Faturamento.Api.Servicos.IA;

/// <summary>
/// Junta as pecas da montagem de nota por texto:
/// busca o catalogo real, manda interpretar, e CONFERE a saida antes de devolver.
///
/// A conferencia e o ponto importante. O que a IA devolve e sugestao, nao verdade:
/// codigo que nao existe no catalogo e descartado, quantidade sem sentido e descartada,
/// e o resultado ainda passa pela pessoa, que confirma na tela antes de virar nota.
/// A IA sugere; quem decide e o humano; quem valida e o servidor.
/// </summary>
public class MontadorDeNotaServico(
    EstoqueClient estoque,
    IInterpretadorDeNota interpretador,
    OpcoesIA opcoes,
    ILogger<MontadorDeNotaServico> log)
{
    private const int QuantidadeMaxima = 100_000;

    public async Task<InterpretacaoResposta> InterpretarAsync(string texto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(texto))
            throw ErroDeNegocioException.Invalido("TEXTO_OBRIGATORIO",
                "Escreva o que voce quer colocar na nota.");

        if (texto.Length > 2000)
            throw ErroDeNegocioException.Invalido("TEXTO_LONGO_DEMAIS",
                "O texto passou de 2000 caracteres. Resuma o pedido.");

        IReadOnlyList<ProdutoRetorno> produtos;
        try
        {
            produtos = await estoque.ListarProdutosAsync(opcoes.MaxProdutosNoPrompt, ct);
        }
        catch (Exception ex)
        {
            // Sem catalogo nao ha o que sugerir. E indisponibilidade, nao erro do usuario.
            log.LogWarning(ex, "Nao consegui buscar o catalogo no Estoque para a interpretacao.");
            throw ErroDeNegocioException.Indisponivel(
                "Nao foi possivel consultar os produtos no servico de Estoque agora. " +
                "Tente montar a nota escolhendo os produtos na mao.");
        }

        if (produtos.Count == 0)
            throw ErroDeNegocioException.Invalido("SEM_PRODUTOS",
                "Nao ha produtos cadastrados para montar uma nota.");

        var catalogo = produtos
            .Select(p => new ProdutoDoCatalogo(p.Id, p.Codigo, p.Descricao, p.Saldo))
            .ToList();

        var crua = await interpretador.InterpretarAsync(texto, catalogo, ct);

        // ---------------- conferencia ----------------
        var porCodigo = catalogo.ToDictionary(p => p.Codigo, StringComparer.OrdinalIgnoreCase);
        var naoEntendidos = crua.NaoEntendidos.ToList();
        var consolidado = new Dictionary<int, ItemSugerido>();

        foreach (var item in crua.Itens)
        {
            // A saida do modelo e texto: qualquer campo pode vir nulo ou em branco,
            // por mais que o formato pedido diga o contrario. Normaliza antes de usar.
            var codigo = item.Codigo?.Trim() ?? "";
            var trecho = string.IsNullOrWhiteSpace(item.Trecho) ? codigo : item.Trecho.Trim();

            if (!porCodigo.TryGetValue(codigo, out var produto))
            {
                // A IA citou um codigo que nao existe. Descarta e registra, sem inventar nada.
                log.LogWarning("A interpretacao sugeriu o codigo inexistente {Codigo}. Descartado.", codigo);
                naoEntendidos.Add(new TrechoNaoEntendido(
                    trecho.Length == 0 ? "?" : trecho,
                    "A sugestao apontou para um produto que nao existe no cadastro."));
                continue;
            }

            if (item.Quantidade is <= 0 or > QuantidadeMaxima)
            {
                naoEntendidos.Add(new TrechoNaoEntendido(trecho,
                    $"Quantidade fora do aceitavel ({item.Quantidade})."));
                continue;
            }

            // Mesmo produto citado duas vezes vira um item so, com a soma.
            if (consolidado.TryGetValue(produto.Id, out var existente))
            {
                consolidado[produto.Id] = existente with
                {
                    Quantidade = existente.Quantidade + item.Quantidade,
                    Trecho = $"{existente.Trecho}; {trecho}",
                    Confianca = Math.Min(existente.Confianca, Arredondar(item.Confianca))
                };
                continue;
            }

            consolidado[produto.Id] = new ItemSugerido(
                produto.Id, produto.Codigo, produto.Descricao,
                item.Quantidade, produto.Saldo,
                Arredondar(item.Confianca), trecho);
        }

        var itens = consolidado.Values.OrderBy(i => i.Codigo).ToList();

        log.LogInformation("Interpretacao ({Modo}): {Itens} item(ns), {NaoEntendidos} trecho(s) sem correspondencia.",
            crua.Modo, itens.Count, naoEntendidos.Count);

        return new InterpretacaoResposta(
            crua.Modo,
            crua.Modelo,
            itens,
            naoEntendidos,
            MontarAviso(crua.Modo, itens));
    }

    private static double Arredondar(double confianca)
        => Math.Round(Math.Clamp(confianca, 0, 1), 2);

    private static string? MontarAviso(string modo, IReadOnlyList<ItemSugerido> itens)
    {
        var avisos = new List<string>();

        if (modo == "offline")
            avisos.Add("Modo offline: nao ha chave de IA configurada, entao os produtos foram " +
                       "casados por semelhanca de texto. Confira com atencao.");

        var acimaDoSaldo = itens.Where(i => i.AcimaDoSaldo).ToList();
        if (acimaDoSaldo.Count > 0)
            avisos.Add("Ja da para ver que " +
                       string.Join(", ", acimaDoSaldo.Select(i => $"{i.Codigo} (pede {i.Quantidade}, tem {i.SaldoAtual})")) +
                       " nao tem saldo suficiente. A nota pode ser criada, mas a impressao vai recusar.");

        return avisos.Count == 0 ? null : string.Join(" ", avisos);
    }
}
