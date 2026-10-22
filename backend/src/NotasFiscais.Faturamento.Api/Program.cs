using FluentValidation;
using NotasFiscais.Faturamento.Api.Api;
using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Dados;
using NotasFiscais.Faturamento.Api.Infra;
using NotasFiscais.Faturamento.Api.Servicos;
using NotasFiscais.Faturamento.Api.Servicos.IA;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

// ---------------------------------------------------------------------------
builder.Services.AddDbContext<FaturamentoDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Faturamento")));

builder.Services.AddScoped<NotaFiscalServico>();
builder.Services.AddScoped<ImpressaoServico>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorGlobalDeErros>();

// ---------------------------------------------------------------------------
// CLIENTE DO ESTOQUE + POLITICA DE RESILIENCIA.
//
// AddStandardResilienceHandler empilha, nesta ordem:
//   timeout total -> disjuntor -> tentativas -> timeout por tentativa
//
// Os numeros abaixo foram escolhidos para caber dentro da paciencia de quem esta
// olhando a tela: no pior caso a espera fica em torno de 7 segundos, com o spinner
// girando, e nao 30 segundos de tela travada.
// ---------------------------------------------------------------------------
var estoqueUrl = builder.Configuration["Estoque:BaseUrl"] ?? "http://localhost:5001";

var resiliencia = builder.Configuration.GetSection("Estoque:Resiliencia").Get<OpcoesResiliencia>()
                  ?? new OpcoesResiliencia();
builder.Services.AddSingleton(resiliencia);

builder.Services.AddHttpClient<EstoqueClient>(c =>
{
    c.BaseAddress = new Uri(estoqueUrl);
    c.DefaultRequestHeaders.Add("User-Agent", "NotasFiscais.Faturamento");
})
.AddStandardResilienceHandler(o =>
{
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(resiliencia.TimeoutPorTentativaSegundos);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(resiliencia.TimeoutTotalSegundos);

    // Tentar de novo, esperando cada vez mais, com uma variacao aleatoria
    // para nao sincronizar varios clientes batendo no mesmo instante.
    o.Retry.MaxRetryAttempts = resiliencia.MaxTentativas;
    o.Retry.Delay = TimeSpan.FromMilliseconds(resiliencia.EsperaInicialMs);
    o.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
    o.Retry.UseJitter = true;

    // Disjuntor: se a proporcao de falhas passar do limite dentro da janela, para de
    // tentar por um tempo e devolve erro na hora, em vez de insistir contra um servico morto.
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(resiliencia.JanelaDisjuntorSegundos);
    o.CircuitBreaker.FailureRatio = resiliencia.ProporcaoFalhaDisjuntor;
    o.CircuitBreaker.MinimumThroughput = resiliencia.MinimoChamadasDisjuntor;
    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(resiliencia.DuracaoAberturaDisjuntorSegundos);
});

// Cliente SEM disjuntor, so para consultas de diagnostico ("essa baixa aconteceu?").
// Ver a explicacao em EstoqueClient.ClienteDeConsulta.
builder.Services.AddHttpClient(EstoqueClient.ClienteDeConsulta, c =>
{
    c.BaseAddress = new Uri(estoqueUrl);
    c.Timeout = TimeSpan.FromSeconds(resiliencia.TimeoutConsultaSegundos);
    c.DefaultRequestHeaders.Add("User-Agent", "NotasFiscais.Faturamento (consulta)");
});

// ---------------------------------------------------------------------------
// MONTAGEM DE NOTA POR TEXTO (inteligencia artificial).
//
// A chave NAO fica no appsettings: vem da variavel de ambiente GEMINI_API_KEY
// ou dos user-secrets. Sem chave, o servico continua de pe e cai no casamento
// offline por semelhanca de texto, avisando na tela que esta nesse modo.
// Quem clonar o projeto consegue rodar tudo sem nenhuma chave.
// ---------------------------------------------------------------------------
var opcoesIA = builder.Configuration.GetSection("IA").Get<OpcoesIA>() ?? new OpcoesIA();
opcoesIA.ApiKey ??= builder.Configuration["GEMINI_API_KEY"];
builder.Services.AddSingleton(opcoesIA);

builder.Services.AddSingleton<InterpretadorOffline>();
builder.Services.AddScoped<MontadorDeNotaServico>();

if (opcoesIA.Habilitada)
{
    builder.Services.AddHttpClient<IInterpretadorDeNota, InterpretadorGemini>(c =>
    {
        c.BaseAddress = new Uri(opcoesIA.UrlBase);
        c.Timeout = TimeSpan.FromSeconds(opcoesIA.TimeoutSegundos);
    });
}
else
{
    builder.Services.AddSingleton<IInterpretadorDeNota>(sp =>
        sp.GetRequiredService<InterpretadorOffline>());
}

// ---------------------------------------------------------------------------
var opcoesReconciliacao = builder.Configuration.GetSection("Reconciliacao").Get<OpcoesReconciliacao>()
                          ?? new OpcoesReconciliacao();
builder.Services.AddSingleton(opcoesReconciliacao);
builder.Services.AddHostedService<ReconciliadorDeNotas>();

// ---------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new()
{
    Title = "Notas Fiscais :: Servico de Faturamento",
    Version = "v1",
    Description = "Dono da nota fiscal. Pede a baixa de saldo ao servico de Estoque."
}));

const string PoliticaCors = "notas-web";
builder.Services.AddCors(o => o.AddPolicy(PoliticaCors, p => p
    .WithOrigins(builder.Configuration.GetSection("OrigensPermitidas").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHealthChecks().AddDbContextCheck<FaturamentoDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseCors(PoliticaCors);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DocumentTitle = "Notas Fiscais :: Faturamento");
}

app.MapHealthChecks("/health");
app.MapNotas();

using (var escopo = app.Services.CreateScope())
{
    var db = escopo.ServiceProvider.GetRequiredService<FaturamentoDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

/// <summary>
/// Marcador para os testes de integracao acharem este projeto (WebApplicationFactory
/// usa o assembly do tipo informado). Uma classe so para isso, em vez de expor o Program,
/// porque os dois servicos teriam um "Program" cada e o projeto de testes referencia os dois.
/// </summary>
public sealed class PontoDeEntradaFaturamento;
