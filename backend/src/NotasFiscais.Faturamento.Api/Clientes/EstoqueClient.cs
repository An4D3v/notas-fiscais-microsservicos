using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotasFiscais.Faturamento.Api.Clientes;

// ---------------------------------------------------------------------------
// Contratos do servico de Estoque, na visao de QUEM CHAMA.
// Repetidos aqui de proposito: os dois servicos nao compartilham projeto nem DLL.
// Se amanha o Estoque virar outra linguagem, so este arquivo muda.
// ---------------------------------------------------------------------------
public record ItemMovimentoEnvio(int ProdutoId, int Quantidade);

public record MovimentarEstoqueEnvio(int NotaFiscalId, int NotaNumero, IReadOnlyList<ItemMovimentoEnvio> Itens);

public record ItemMovimentoRetorno(
    int ProdutoId, string Codigo, string Descricao, int Quantidade, int SaldoResultante);

public record MovimentoRetorno(
    int MovimentoId, string ChaveIdempotencia, string Tipo,
    int NotaFiscalId, int NotaNumero, DateTime OcorridoEm,
    IReadOnlyList<ItemMovimentoRetorno> Itens, bool Repetido);

public record ProdutoRetorno(int Id, string Codigo, string Descricao, int Saldo);

public record PaginaRetorno<T>(IReadOnlyList<T> Itens, int Total, int Pagina, int Tamanho);

/// <summary>
/// Resultado de uma chamada ao Estoque, ja separado nos tres desfechos que importam.
/// Isso evita o vicio de tratar "saldo insuficiente" e "servico caiu" com o mesmo catch:
/// o primeiro e resposta legitima do negocio, o segundo e falha de infraestrutura.
/// </summary>
public abstract record ResultadoEstoque
{
    public sealed record Sucesso(MovimentoRetorno Movimento) : ResultadoEstoque;

    /// <summary>O Estoque respondeu, e a resposta foi "nao": saldo insuficiente, produto inexistente...</summary>
    public sealed record RecusadoPeloNegocio(int Status, string Codigo, string Mensagem, JsonElement? Detalhes)
        : ResultadoEstoque;

    /// <summary>O Estoque nao respondeu: fora do ar, timeout, disjuntor aberto, erro 5xx.</summary>
    public sealed record Indisponivel(string Motivo) : ResultadoEstoque;
}

public class EstoqueClient(HttpClient http, IHttpClientFactory fabrica, ILogger<EstoqueClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Nome do cliente HTTP usado apenas para CONSULTAS, sem disjuntor.
    ///
    /// Por que separar: o disjuntor existe para parar de martelar um servico que esta sofrendo.
    /// Mas a consulta "essa baixa aconteceu?" e justamente o que precisamos quando as coisas
    /// deram errado, e ela e barata (um GET). Se ela ficasse atras do mesmo disjuntor,
    /// uma nota presa continuaria presa mesmo com o Estoque ja recuperado.
    /// </summary>
    public const string ClienteDeConsulta = "estoque-consulta";

    private HttpClient Consulta => fabrica.CreateClient(ClienteDeConsulta);

    public async Task<ResultadoEstoque> BaixarAsync(
        string chaveIdempotencia, MovimentarEstoqueEnvio envio, CancellationToken ct)
        => await ChamarAsync("/estoque/baixas", chaveIdempotencia, envio, ct);

    public async Task<ResultadoEstoque> EstornarAsync(
        string chaveIdempotencia, MovimentarEstoqueEnvio envio, CancellationToken ct)
        => await ChamarAsync("/estoque/estornos", chaveIdempotencia, envio, ct);

    private async Task<ResultadoEstoque> ChamarAsync(
        string rota, string chave, MovimentarEstoqueEnvio envio, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, rota)
            {
                Content = JsonContent.Create(envio, options: Json)
            };
            // A MESMA chave em toda tentativa. E o que torna o retry seguro:
            // se a primeira tentativa chegou a aplicar, a segunda so recebe a resposta de volta.
            req.Headers.Add("Idempotency-Key", chave);

            using var resp = await http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                var mov = await resp.Content.ReadFromJsonAsync<MovimentoRetorno>(Json, ct);
                return mov is null
                    ? new ResultadoEstoque.Indisponivel("O Estoque respondeu com corpo vazio.")
                    : new ResultadoEstoque.Sucesso(mov);
            }

            // 4xx = o Estoque entendeu e recusou. Isso e regra de negocio, nao falha.
            if ((int)resp.StatusCode is >= 400 and < 500)
            {
                var (codigo, mensagem, detalhes) = await LerProblemaAsync(resp, ct);
                log.LogWarning("Estoque recusou {Rota}: {Codigo} - {Mensagem}", rota, codigo, mensagem);
                return new ResultadoEstoque.RecusadoPeloNegocio((int)resp.StatusCode, codigo, mensagem, detalhes);
            }

            log.LogError("Estoque respondeu {Status} em {Rota}.", (int)resp.StatusCode, rota);
            return new ResultadoEstoque.Indisponivel($"O Estoque respondeu HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested is false)
        {
            // Cai aqui: conexao recusada, timeout de tentativa, disjuntor aberto.
            // ATENCAO: TaskCanceledException herda de OperationCanceledException. Sem o filtro
            // acima, um timeout do HttpClient seria confundido com "o usuario cancelou".
            log.LogError(ex, "Falha de comunicacao com o Estoque em {Rota}.", rota);
            return new ResultadoEstoque.Indisponivel(DescreverFalha(ex));
        }
    }

    /// <summary>
    /// Pergunta ao Estoque se um movimento com aquela chave existe.
    /// Usado quando perdemos a resposta e precisamos descobrir se a baixa aconteceu.
    /// </summary>
    public async Task<MovimentoRetorno?> ConsultarMovimentoAsync(string chave, CancellationToken ct)
    {
        var resp = await Consulta.GetAsync($"/estoque/movimentos/{Uri.EscapeDataString(chave)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<MovimentoRetorno>(Json, ct);
    }

    /// <summary>
    /// Traz o catalogo para a interpretacao por texto. Vai pelo cliente de consulta
    /// (sem disjuntor) porque e leitura, e nao muda nada do outro lado.
    /// </summary>
    public async Task<IReadOnlyList<ProdutoRetorno>> ListarProdutosAsync(int tamanho, CancellationToken ct)
    {
        var limite = Math.Clamp(tamanho, 1, 200);
        var resp = await Consulta.GetAsync($"/produtos?pagina=1&tamanho={limite}", ct);
        resp.EnsureSuccessStatusCode();

        var pagina = await resp.Content.ReadFromJsonAsync<PaginaRetorno<ProdutoRetorno>>(Json, ct);
        return pagina?.Itens ?? [];
    }

    /// <summary>Consulta produtos por id (validacao best-effort na criacao da nota).</summary>
    public async Task<ProdutoRetorno?> ConsultarProdutoAsync(int id, CancellationToken ct)
    {
        var resp = await Consulta.GetAsync($"/produtos/{id}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProdutoRetorno>(Json, ct);
    }

    private static async Task<(string, string, JsonElement?)> LerProblemaAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var raiz = doc.RootElement;

            var codigo = raiz.TryGetProperty("codigo", out var c) ? c.GetString() ?? "ERRO" : "ERRO";
            var mensagem = raiz.TryGetProperty("title", out var t)
                ? t.GetString() ?? "O Estoque recusou a operacao."
                : "O Estoque recusou a operacao.";
            JsonElement? detalhes = raiz.TryGetProperty("detalhes", out var d) ? d.Clone() : null;

            return (codigo, mensagem, detalhes);
        }
        catch
        {
            return ("ERRO", "O Estoque recusou a operacao.", null);
        }
    }

    private static string DescreverFalha(Exception ex) => ex switch
    {
        // O disjuntor aberto vem antes: ele tambem se manifesta como excecao, e a mensagem
        // precisa explicar que o sistema PAROU DE TENTAR de proposito, e nao que deu pau.
        _ when ex.GetType().Name.Contains("BrokenCircuit") =>
            "O servico de Estoque falhou varias vezes seguidas, entao as chamadas foram suspensas " +
            "por alguns segundos para ele se recuperar. Tente novamente em instantes.",

        TaskCanceledException => "O Estoque nao respondeu dentro do tempo limite.",
        HttpRequestException => "Nao foi possivel conectar ao servico de Estoque.",
        _ => "Falha de comunicacao com o servico de Estoque."
    };
}
