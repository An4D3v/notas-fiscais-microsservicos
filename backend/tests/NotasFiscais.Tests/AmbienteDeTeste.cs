using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NotasFiscais.Estoque.Api.Dados;
using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Dados;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NotasFiscais.Tests;

/// <summary>
/// Sobe os DOIS servicos de verdade, um falando com o outro, contra um SQL Server real.
///
/// Por que SQL Server real e nao banco em memoria: metade do que estes testes provam
/// (concorrencia, indice unico, UPDATE atomico, travamento de linha) SO EXISTE num
/// banco relacional de verdade. Com o provedor em memoria do EF, o teste de concorrencia
/// passaria sempre e nao provaria absolutamente nada.
///
/// Os dois servidores rodam em processo (TestServer). O cliente HTTP do Faturamento e
/// religado no servidor de teste do Estoque, entao a conversa entre eles e a real,
/// passando por serializacao, headers e status HTTP, sem precisar de porta aberta.
/// </summary>
public class AmbienteDeTeste : IAsyncLifetime
{
    /// <summary>
    /// Servidor usado pelos testes. O padrao e a instancia LocalDB que vem com o
    /// Visual Studio / SQL Server Express, entao quem clonar o repositorio roda
    /// os testes sem configurar nada.
    ///
    /// Para apontar para outro servidor (outra instancia LocalDB, SQL Server em
    /// container, etc.), defina a variavel de ambiente NOTAS_TEST_SQL.
    /// </summary>
    private static readonly string Instancia =
        Environment.GetEnvironmentVariable("NOTAS_TEST_SQL")
        ?? @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    public static readonly string ConexaoEstoque = $"{Instancia};Database=NotasTestes_Estoque";
    public static readonly string ConexaoFaturamento = $"{Instancia};Database=NotasTestes_Faturamento";

    public WebApplicationFactory<PontoDeEntradaEstoque> AppEstoque { get; private set; } = null!;
    public WebApplicationFactory<PontoDeEntradaFaturamento> AppFaturamento { get; private set; } = null!;

    public HttpClient Estoque { get; private set; } = null!;
    public HttpClient Faturamento { get; private set; } = null!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        await ApagarBancosAsync();

        AppEstoque = new WebApplicationFactory<PontoDeEntradaEstoque>()
            .WithWebHostBuilder(b =>
            {
                // Development para que o interruptor de caos (/admin/caos) exista.
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Estoque", ConexaoEstoque);
            });

        Estoque = AppEstoque.CreateClient();

        AppFaturamento = new WebApplicationFactory<PontoDeEntradaFaturamento>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Faturamento", ConexaoFaturamento);

                // Disjuntor curto: o teste que derruba o Estoque precisa provar que ele
                // VOLTA. Com os 10 segundos de producao, a suite ficaria parada esperando.
                b.UseSetting("Estoque:Resiliencia:DuracaoAberturaDisjuntorSegundos", "1");
                b.UseSetting("Estoque:Resiliencia:EsperaInicialMs", "40");
                b.UseSetting("Estoque:Resiliencia:TimeoutPorTentativaSegundos", "3");

                // Reconciliador praticamente desligado no ambiente compartilhado, para
                // ele nao mexer numa nota no meio de outro teste. O teste do reconciliador
                // sobe a propria instancia com o tempo curto.
                b.UseSetting("Reconciliacao:IdadeMinimaSegundos", "36000");
                b.UseSetting("Reconciliacao:IntervaloSegundos", "3600");

                b.ConfigureServices(s => LigarNoEstoqueDeTeste(s, AppEstoque));
            });

        Faturamento = AppFaturamento.CreateClient();
    }

    /// <summary>
    /// Troca o cano de saida dos clientes HTTP do Faturamento pelo servidor de teste
    /// do Estoque. A politica de resiliencia registrada pelo proprio servico continua
    /// valendo: so o transporte muda.
    /// </summary>
    public static void LigarNoEstoqueDeTeste(
        IServiceCollection servicos, WebApplicationFactory<PontoDeEntradaEstoque> estoque)
    {
        servicos.AddHttpClient<EstoqueClient>()
            .ConfigurePrimaryHttpMessageHandler(() => estoque.Server.CreateHandler());

        servicos.AddHttpClient(EstoqueClient.ClienteDeConsulta)
            .ConfigurePrimaryHttpMessageHandler(() => estoque.Server.CreateHandler());
    }

    private static async Task ApagarBancosAsync()
    {
        var opcoesEstoque = new DbContextOptionsBuilder<EstoqueDbContext>()
            .UseSqlServer(ConexaoEstoque).Options;
        await using (var db = new EstoqueDbContext(opcoesEstoque))
            await db.Database.EnsureDeletedAsync();

        var opcoesFaturamento = new DbContextOptionsBuilder<FaturamentoDbContext>()
            .UseSqlServer(ConexaoFaturamento).Options;
        await using (var db = new FaturamentoDbContext(opcoesFaturamento))
            await db.Database.EnsureDeletedAsync();
    }

    public async Task DisposeAsync()
    {
        Estoque?.Dispose();
        Faturamento?.Dispose();
        if (AppFaturamento is not null) await AppFaturamento.DisposeAsync();
        if (AppEstoque is not null) await AppEstoque.DisposeAsync();
    }

    // ==================================================================
    // Atalhos usados pelos testes
    // ==================================================================

    /// <summary>Cria um produto com codigo unico, para um teste nunca depender de outro.</summary>
    public async Task<ProdutoDto> CriarProdutoAsync(int saldo, string? prefixo = null)
    {
        var codigo = $"{prefixo ?? "T"}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var resposta = await Estoque.PostAsJsonAsync("/produtos", new
        {
            codigo,
            descricao = $"Produto de teste {codigo}",
            saldo,
        });

        resposta.EnsureSuccessStatusCode();
        return (await resposta.Content.ReadFromJsonAsync<ProdutoDto>(Json))!;
    }

    public async Task<int> SaldoAsync(int produtoId)
    {
        var p = await Estoque.GetFromJsonAsync<ProdutoDto>($"/produtos/{produtoId}", Json);
        return p!.Saldo;
    }

    public async Task<NotaDto> CriarNotaAsync(params (ProdutoDto produto, int quantidade)[] itens)
    {
        var corpo = new
        {
            itens = itens.Select(i => new
            {
                produtoId = i.produto.Id,
                codigo = i.produto.Codigo,
                descricao = i.produto.Descricao,
                quantidade = i.quantidade,
            }),
        };

        var resposta = await Faturamento.PostAsJsonAsync("/notas", corpo);
        resposta.EnsureSuccessStatusCode();
        return (await resposta.Content.ReadFromJsonAsync<NotaDto>(Json))!;
    }

    public Task<HttpResponseMessage> ImprimirAsync(int notaId, string chave)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/notas/{notaId}/imprimir")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add("Idempotency-Key", chave);
        return Faturamento.SendAsync(req);
    }

    public Task<HttpResponseMessage> BaixarAsync(string chave, object corpo)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/estoque/baixas")
        {
            Content = JsonContent.Create(corpo),
        };
        req.Headers.Add("Idempotency-Key", chave);
        return Estoque.SendAsync(req);
    }

    /// <summary>Liga ou desliga a simulacao de indisponibilidade do Estoque.</summary>
    public async Task CaosAsync(bool ativo)
    {
        var r = await Estoque.PostAsync($"/admin/caos?ativo={ativo.ToString().ToLowerInvariant()}", null);
        r.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Devolve o ambiente ao normal depois de um teste que derrubou o Estoque.
    ///
    /// Desligar o caos NAO basta. O disjuntor abriu por causa das falhas e, mesmo depois
    /// de fechar o tempo, ele passa por um estado MEIO-ABERTO em que deixa passar UMA
    /// unica chamada de prova e recusa as demais na hora. Um teste seguinte que dispare
    /// duas chamadas ao mesmo tempo (o de concorrencia, por exemplo) veria uma delas
    /// recusada por um motivo que nada tem a ver com o que ele esta testando.
    ///
    /// Por isso o encerramento faz uma impressao de verdade ate dar certo: e ela que
    /// serve de chamada de prova e fecha o disjuntor de vez.
    /// </summary>
    public async Task RestaurarEstoqueAsync()
    {
        await CaosAsync(false);

        var produto = await CriarProdutoAsync(saldo: 1, prefixo: "AQUECE");
        var nota = await CriarNotaAsync((produto, 1));

        for (var tentativa = 0; tentativa < 25; tentativa++)
        {
            var r = await ImprimirAsync(nota.Id, $"aquecimento-{nota.Id}");
            if (r.StatusCode == HttpStatusCode.OK) return;
            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            "O disjuntor nao voltou a fechar depois do teste de indisponibilidade.");
    }

    /// <summary>Le o campo "codigo" do corpo de erro (ProblemDetails) devolvido pelas APIs.</summary>
    public static async Task<string> CodigoDoErroAsync(HttpResponseMessage resposta)
    {
        using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("codigo", out var c) ? c.GetString() ?? "" : "";
    }
}

[CollectionDefinition("ambiente")]
public class ColecaoAmbiente : ICollectionFixture<AmbienteDeTeste>;

// ---------- Formatos de leitura usados pelos testes ----------

public record ProdutoDto(int Id, string Codigo, string Descricao, int Saldo);

public record ItemNotaDto(int Id, int ProdutoId, string Codigo, string Descricao, int Quantidade);

public record NotaDto(
    int Id, int Numero, string Status, DateTime CriadaEm,
    DateTime? ImpressaEm, string? UltimoErro, List<ItemNotaDto> Itens);

public record SaldoAtualizadoDto(
    int ProdutoId, string Codigo, string Descricao, int Quantidade, int SaldoResultante);

public record ImpressaoDto(NotaDto Nota, List<SaldoAtualizadoDto> SaldosAtualizados, bool Repetido);

public record MovimentoDto(
    int MovimentoId, string ChaveIdempotencia, string Tipo,
    int NotaFiscalId, int NotaNumero, DateTime OcorridoEm,
    List<SaldoAtualizadoDto> Itens, bool Repetido);
