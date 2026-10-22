using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Dados;
using NotasFiscais.Faturamento.Api.Dominio;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Faturamento.Api.Servicos;

public class OpcoesReconciliacao
{
    /// <summary>De quanto em quanto tempo a varredura roda.</summary>
    public int IntervaloSegundos { get; set; } = 20;

    /// <summary>
    /// Idade minima da nota em Processando para ela ser considerada "presa".
    /// Precisa ser MAIOR que o tempo total que uma impressao normal pode levar
    /// (tentativas + esperas), senao o reconciliador atropela uma impressao em andamento.
    /// </summary>
    public int IdadeMinimaSegundos { get; set; } = 60;
}

/// <summary>
/// A rede de seguranca do sistema.
///
/// Quando a impressao perde a resposta do Estoque E nao consegue nem perguntar o que aconteceu,
/// a nota fica em "Processando". Este servico roda em segundo plano, encontra essas notas
/// e pergunta ao Estoque: "a baixa com a chave X aconteceu?".
///   - aconteceu  -> fecha a nota (o saldo ja saiu, a nota tem que refletir isso);
///   - nao aconteceu -> devolve a nota para Aberta, para a pessoa tentar de novo.
///
/// Sem isso, uma queda de rede de 2 segundos deixaria a nota presa para sempre,
/// e alguem teria que arrumar na mao, no banco.
/// </summary>
public class ReconciliadorDeNotas(
    IServiceScopeFactory escopos,
    OpcoesReconciliacao opcoes,
    ILogger<ReconciliadorDeNotas> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        log.LogInformation("Reconciliador ligado: varredura a cada {Intervalo}s, notas presas ha mais de {Idade}s.",
            opcoes.IntervaloSegundos, opcoes.IdadeMinimaSegundos);

        while (!parada.IsCancellationRequested)
        {
            try
            {
                await VarrerAsync(parada);
            }
            catch (OperationCanceledException) when (parada.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Um erro numa rodada nao pode matar o servico de fundo.
                log.LogError(ex, "Falha na varredura de reconciliacao. Tentando de novo na proxima rodada.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(opcoes.IntervaloSegundos), parada);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.LogInformation("Reconciliador encerrado.");
    }

    private async Task VarrerAsync(CancellationToken ct)
    {
        using var escopo = escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<FaturamentoDbContext>();
        var estoque = escopo.ServiceProvider.GetRequiredService<EstoqueClient>();

        var corte = DateTime.UtcNow.AddSeconds(-opcoes.IdadeMinimaSegundos);

        var presas = await db.Notas
            .Where(n => n.Status == StatusNota.Processando
                     && n.ChaveImpressao != null
                     && n.AtualizadaEm < corte)
            .OrderBy(n => n.AtualizadaEm)
            .Take(50)
            .ToListAsync(ct);

        if (presas.Count == 0) return;

        log.LogWarning("Reconciliacao: {Qtd} nota(s) presa(s) em Processando.", presas.Count);

        foreach (var nota in presas)
        {
            MovimentoRetorno? movimento;
            try
            {
                movimento = await estoque.ConsultarMovimentoAsync(nota.ChaveImpressao!, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Estoque ainda indisponivel: nota {Numero} continua presa.", nota.Numero);
                continue; // tenta de novo na proxima rodada
            }

            if (movimento is not null)
            {
                nota.Status = StatusNota.Fechada;
                nota.ImpressaEm = movimento.OcorridoEm;
                nota.UltimoErro = null;
                log.LogInformation("Reconciliacao: nota {Numero} FECHADA (a baixa tinha acontecido).", nota.Numero);
            }
            else
            {
                nota.Status = StatusNota.Aberta;
                nota.ChaveImpressao = null;
                nota.UltimoErro = "A impressao anterior nao foi concluida. Nenhum saldo foi alterado.";
                log.LogInformation("Reconciliacao: nota {Numero} devolvida para Aberta (a baixa nao aconteceu).",
                    nota.Numero);
            }

            nota.AtualizadaEm = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Alguem mexeu na nota entre a leitura e a gravacao. Deixa para a proxima rodada.
                db.Entry(nota).State = EntityState.Detached;
                log.LogInformation("Nota {Numero} mudou durante a reconciliacao; pulando.", nota.Numero);
            }
        }
    }
}
