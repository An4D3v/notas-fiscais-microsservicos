using System.Net.Http.Json;
using System.Text.Json;

namespace NotasFiscais.Faturamento.Api.Servicos.IA;

/// <summary>
/// Interpretacao com modelo de linguagem (Google Gemini).
///
/// Tres cuidados que valem mais que a chamada em si:
///
/// 1. RESPOSTA EM FORMATO FIXO. O pedido leva um responseSchema, entao o modelo devolve
///    JSON com a forma que combinamos, em vez de texto livre que a gente teria que adivinhar.
///
/// 2. O MODELO NAO INVENTA PRODUTO. Ele so pode escolher codigos da lista que mandamos, e
///    quem confere isso e o servidor depois (ver MontadorDeNotaServico). Se ele devolver um
///    codigo que nao existe, o item e descartado. Nunca confiamos na saida direto.
///
/// 3. SE FALHAR, NAO QUEBRA. Qualquer erro (sem internet, cota estourada, JSON torto)
///    cai no interpretador offline. A pessoa continua conseguindo montar a nota.
/// </summary>
public class InterpretadorGemini(
    HttpClient http,
    OpcoesIA opcoes,
    InterpretadorOffline reserva,
    ILogger<InterpretadorGemini> log) : IInterpretadorDeNota
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<InterpretacaoCrua> InterpretarAsync(
        string texto, IReadOnlyList<ProdutoDoCatalogo> catalogo, CancellationToken ct)
    {
        try
        {
            return await ChamarModeloAsync(texto, catalogo, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A IA falhou. Caindo no casamento offline para nao travar a tela.");
            return await reserva.InterpretarAsync(texto, catalogo, ct);
        }
    }

    private async Task<InterpretacaoCrua> ChamarModeloAsync(
        string texto, IReadOnlyList<ProdutoDoCatalogo> catalogo, CancellationToken ct)
    {
        var lista = string.Join("\n", catalogo
            .Take(opcoes.MaxProdutosNoPrompt)
            .Select(p => $"{p.Codigo} | {p.Descricao} | saldo {p.Saldo}"));

        var instrucao =
            "Voce converte um pedido escrito em portugues em itens de nota fiscal.\n" +
            "Use SOMENTE produtos da lista abaixo. NUNCA invente codigo.\n" +
            "Para cada item devolva: codigo (exatamente como na lista), quantidade (inteiro maior que zero), " +
            "trecho (o pedaco do texto original que gerou o item) e confianca (0 a 1).\n" +
            "Se um pedaco do texto nao corresponder a nenhum produto da lista, coloque em naoEntendidos " +
            "com o motivo, em vez de escolher um produto parecido no chute.\n" +
            "Se a mesma coisa aparecer mais de uma vez, some as quantidades num item so.\n" +
            "Ignore saldo ao decidir: quem confere estoque e o sistema, nao voce.\n\n" +
            "PRODUTOS DISPONIVEIS (codigo | descricao | saldo):\n" + lista;

        var corpo = new
        {
            systemInstruction = new { parts = new[] { new { text = instrucao } } },
            contents = new[] { new { parts = new[] { new { text = texto } } } },
            generationConfig = new
            {
                temperature = 0,
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        itens = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    codigo = new { type = "string" },
                                    quantidade = new { type = "integer" },
                                    trecho = new { type = "string" },
                                    confianca = new { type = "number" }
                                },
                                required = new[] { "codigo", "quantidade", "trecho", "confianca" }
                            }
                        },
                        naoEntendidos = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    trecho = new { type = "string" },
                                    motivo = new { type = "string" }
                                },
                                required = new[] { "trecho", "motivo" }
                            }
                        }
                    },
                    required = new[] { "itens", "naoEntendidos" }
                }
            }
        };

        var rota = $"/v1beta/models/{opcoes.Modelo}:generateContent?key={opcoes.ApiKey}";
        using var resposta = await http.PostAsJsonAsync(rota, corpo, Json, ct);

        if (!resposta.IsSuccessStatusCode)
        {
            var erro = await resposta.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gemini respondeu {(int)resposta.StatusCode}: {Resumir(erro)}");
        }

        var envelope = await resposta.Content.ReadFromJsonAsync<GeminiEnvelope>(Json, ct);
        var conteudo = envelope?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(conteudo))
            throw new InvalidOperationException("Gemini devolveu resposta vazia.");

        var interpretacao = JsonSerializer.Deserialize<SaidaDoModelo>(conteudo, Json)
            ?? throw new InvalidOperationException("Nao consegui ler o JSON devolvido pela IA.");

        return new InterpretacaoCrua(
            "ia",
            opcoes.Modelo,
            (interpretacao.Itens ?? [])
                .Select(i => new ItemCru(i.Codigo ?? "", i.Quantidade, i.Trecho ?? "", i.Confianca))
                .ToList(),
            (interpretacao.NaoEntendidos ?? [])
                .Select(n => new TrechoNaoEntendido(n.Trecho ?? "", n.Motivo ?? "Nao identificado."))
                .ToList());
    }

    private static string Resumir(string texto) =>
        texto.Length <= 300 ? texto : texto[..300] + "...";

    // ---------- formatos de leitura da resposta do Gemini ----------
    private record GeminiEnvelope(List<GeminiCandidato>? Candidates);
    private record GeminiCandidato(GeminiConteudo? Content);
    private record GeminiConteudo(List<GeminiParte>? Parts);
    private record GeminiParte(string? Text);

    private record SaidaDoModelo(List<ItemDoModelo>? Itens, List<NaoEntendidoDoModelo>? NaoEntendidos);
    private record ItemDoModelo(string? Codigo, int Quantidade, string? Trecho, double Confianca);
    private record NaoEntendidoDoModelo(string? Trecho, string? Motivo);
}
