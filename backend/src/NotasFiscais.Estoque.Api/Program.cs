using FluentValidation;
using NotasFiscais.Estoque.Api.Api;
using NotasFiscais.Estoque.Api.Dados;
using NotasFiscais.Estoque.Api.Dominio;
using NotasFiscais.Estoque.Api.Infra;
using NotasFiscais.Estoque.Api.Servicos;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Log estruturado. Cada linha sai com o contexto (rota, tempo, status),
// o que torna possivel explicar depois por que uma nota falhou.
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"));

// ---------------------------------------------------------------------------
// Servicos
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<EstoqueDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Estoque")));

builder.Services.AddScoped<EstoqueServico>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddSingleton<ModoCaos>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorGlobalDeErros>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new()
{
    Title = "Notas Fiscais :: Servico de Estoque",
    Version = "v1",
    Description = "Dono do saldo dos produtos. Nenhum outro servico escreve nesta base."
}));

// O front roda em outra porta, entao o navegador exige CORS liberado explicitamente.
const string PoliticaCors = "notas-web";
builder.Services.AddCors(o => o.AddPolicy(PoliticaCors, p => p
    .WithOrigins(builder.Configuration.GetSection("OrigensPermitidas").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHealthChecks().AddDbContextCheck<EstoqueDbContext>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseCors(PoliticaCors);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DocumentTitle = "Notas Fiscais :: Estoque");

    // Simulacao de indisponibilidade: quando o modo caos esta ligado, o servico
    // responde 503 como se estivesse fora do ar. Usado nos testes e na demonstracao.
    app.Use(async (ctx, next) =>
    {
        var caos = ctx.RequestServices.GetRequiredService<ModoCaos>();
        var rota = ctx.Request.Path.Value ?? string.Empty;
        var rotaProtegida = rota.StartsWith("/estoque") || rota.StartsWith("/produtos");

        if (caos.Ativo && rotaProtegida)
        {
            if (caos.AtrasoMs > 0)
                await Task.Delay(caos.AtrasoMs, ctx.RequestAborted);

            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new
            {
                codigo = "SERVICO_INDISPONIVEL",
                title = "Servico de Estoque temporariamente indisponivel (modo caos ligado)."
            });
            return;
        }

        await next();
    });

    app.MapCaos();
}

app.MapHealthChecks("/health");
app.MapProdutos();
app.MapEstoque();

// ---------------------------------------------------------------------------
// Sobe o banco e semeia dados. Deixa quem clonar rodar o projeto sem
// nenhum passo manual de banco.
// ---------------------------------------------------------------------------
await PrepararBancoAsync(app);

app.Run();

static async Task PrepararBancoAsync(WebApplication app)
{
    using var escopo = app.Services.CreateScope();
    var db = escopo.ServiceProvider.GetRequiredService<EstoqueDbContext>();
    var log = escopo.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await MigrarAsync(db.Database, log);

    if (!await db.Produtos.AnyAsync())
    {
        db.Produtos.AddRange(
            new Produto { Codigo = "P-001", Descricao = "Caneta esferografica azul", Saldo = 10 },
            new Produto { Codigo = "P-002", Descricao = "Caneta esferografica preta", Saldo = 25 },
            new Produto { Codigo = "P-003", Descricao = "Caderno universitario 96 folhas", Saldo = 8 },
            new Produto { Codigo = "P-004", Descricao = "Resma de papel A4 500 folhas", Saldo = 40 },
            new Produto { Codigo = "P-005", Descricao = "Grampeador de mesa 26/6", Saldo = 5 },
            new Produto { Codigo = "P-006", Descricao = "Item raro (para testar concorrencia)", Saldo = 1 });

        await db.SaveChangesAsync();
        log.LogInformation("Banco semeado com produtos de exemplo.");
    }
}

/// <summary>
/// Aplica as migrations esperando o banco acordar. No Azure SQL serverless o banco
/// pausa quando ninguem usa; a primeira conexao o acorda e e recusada enquanto isso
/// (erros 40613 e vizinhos). Sem a espera, o servico cairia logo na subida.
/// </summary>
static async Task MigrarAsync(DatabaseFacade banco, Microsoft.Extensions.Logging.ILogger log)
{
    for (var tentativa = 1; ; tentativa++)
    {
        try
        {
            await banco.MigrateAsync();
            return;
        }
        catch (SqlException e) when (tentativa < 12 && e.Number is 40613 or 40197 or 40501 or 49918 or 49919 or 49920 or -2)
        {
            log.LogWarning("Banco indisponivel (erro {Numero}), provavelmente acordando. Nova tentativa em 10 s.", e.Number);
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
}

/// <summary>
/// Marcador para os testes de integracao acharem este projeto (WebApplicationFactory
/// usa o assembly do tipo informado). Uma classe so para isso, em vez de expor o Program,
/// porque os dois servicos teriam um "Program" cada e o projeto de testes referencia os dois.
/// </summary>
public sealed class PontoDeEntradaEstoque;
