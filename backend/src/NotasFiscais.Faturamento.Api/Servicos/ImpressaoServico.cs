using NotasFiscais.Faturamento.Api.Clientes;
using NotasFiscais.Faturamento.Api.Contratos;
using NotasFiscais.Faturamento.Api.Dados;
using NotasFiscais.Faturamento.Api.Dominio;
using NotasFiscais.Faturamento.Api.Infra;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Faturamento.Api.Servicos;

/// <summary>
/// A IMPRESSAO DA NOTA. E aqui que os dois servicos precisam concordar sobre a realidade.
///
/// O problema: fechar a nota (banco do Faturamento) e baixar o saldo (banco do Estoque)
/// sao duas escritas em bancos diferentes. Nao existe um COMMIT que cubra os dois.
/// Se der errado no meio, um lado pode ficar diferente do outro.
///
/// A solucao (padrao SAGA com compensacao) tem quatro partes:
///   1. antes de chamar o Estoque, a nota vai para "Processando" e guarda a chave da tentativa;
///   2. a chamada ao Estoque leva essa chave, entao repetir e seguro (idempotencia);
///   3. se o Estoque recusa (saldo insuficiente), a nota volta para Aberta com o motivo;
///   4. se PERDEMOS A RESPOSTA, nao adivinhamos: perguntamos ao Estoque se a baixa aconteceu.
///      Se nem perguntar der certo, a nota FICA em Processando e o reconciliador resolve depois.
///
/// O erro classico aqui e, ao perder a resposta, devolver a nota para Aberta "por seguranca".
/// Isso pode deixar saldo baixado com nota aberta: o pior dos dois mundos.
/// </summary>
public class ImpressaoServico(
    FaturamentoDbContext db,
    EstoqueClient estoque,
    NotaFiscalServico notas,
    ILogger<ImpressaoServico> log)
{
    public async Task<ImpressaoResposta> ImprimirAsync(int notaId, string chave, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chave))
            throw ErroDeNegocioException.Invalido("CHAVE_IDEMPOTENCIA_OBRIGATORIA",
                "Envie o header Idempotency-Key ao imprimir.");

        var nota = await notas.CarregarAsync(notaId, ct);

        // ---------------------------------------------------------------
        // Caso 1: ja imprimimos esta nota com ESTA MESMA chave.
        // O usuario clicou duas vezes ou a resposta se perdeu no caminho de volta.
        // Devolvemos o mesmo resultado, sem tocar em nada.
        // ---------------------------------------------------------------
        if (nota.Status == StatusNota.Fechada && nota.ChaveImpressao == chave)
        {
            var movimento = await estoque.ConsultarMovimentoAsync(chave, ct);
            log.LogInformation("Nota {Numero} ja impressa com a chave {Chave}: repetindo resposta.",
                nota.Numero, chave);

            return new ImpressaoResposta(
                NotaFiscalServico.Mapear(nota),
                MapearSaldos(movimento),
                Repetido: true);
        }

        if (nota.Status == StatusNota.Fechada)
            throw ErroDeNegocioException.Conflito("NOTA_JA_IMPRESSA",
                $"A nota {nota.Numero} ja foi impressa em {nota.ImpressaEm:dd/MM/yyyy HH:mm} e nao pode ser impressa de novo.",
                new { notaId = nota.Id, numero = nota.Numero, status = nota.Status.ToString() });

        // ---------------------------------------------------------------
        // Caso 2: a nota ficou presa em "Processando" numa tentativa anterior.
        //
        // Em vez de simplesmente recusar e mandar a pessoa esperar o reconciliador,
        // aproveitamos o clique dela para RESOLVER a pendencia: perguntamos ao Estoque
        // o que aconteceu com a tentativa antiga. O sistema se cura no proximo clique,
        // e nao so no proximo ciclo do servico de fundo.
        // ---------------------------------------------------------------
        if (nota.Status == StatusNota.Processando)
        {
            var resolvida = await ResolverPendenciaAsync(nota, ct);
            if (resolvida is not null) return resolvida; // a baixa antiga existia: nota fechada
            // se voltou null, a nota foi devolvida para Aberta e a impressao segue normalmente
        }

        if (nota.Itens.Count == 0)
            throw ErroDeNegocioException.Invalido("NOTA_SEM_ITENS",
                $"A nota {nota.Numero} nao tem produtos e nao pode ser impressa.");

        // ---------------------------------------------------------------
        // Passo 1: marcar "Processando" ANTES de sair pedindo baixa.
        // Se o processo morrer no proximo milissegundo, quem chegar depois
        // sabe exatamente em que pe a coisa estava, e com qual chave.
        // ---------------------------------------------------------------
        nota.Status = StatusNota.Processando;
        nota.ChaveImpressao = chave;
        nota.UltimoErro = null;
        nota.AtualizadaEm = DateTime.UtcNow;
        await SalvarComControleDeVersaoAsync(nota, ct);

        // ---------------------------------------------------------------
        // Passo 2: pedir a baixa. A politica de resiliencia (tentativas + disjuntor)
        // esta configurada no HttpClient, entao aqui o codigo fica limpo.
        // ---------------------------------------------------------------
        var envio = new MovimentarEstoqueEnvio(
            nota.Id, nota.Numero,
            nota.Itens.Select(i => new ItemMovimentoEnvio(i.ProdutoId, i.Quantidade)).ToList());

        var resultado = await estoque.BaixarAsync(chave, envio, ct);

        return resultado switch
        {
            ResultadoEstoque.Sucesso s => await FecharAsync(nota, s.Movimento, ct),
            ResultadoEstoque.RecusadoPeloNegocio r => await ReabrirPorRecusaAsync(nota, r, ct),
            ResultadoEstoque.Indisponivel i => await ResolverIncertezaAsync(nota, chave, i, ct),
            _ => throw new InvalidOperationException("Resultado de estoque desconhecido.")
        };
    }

    // ==================================================================
    // Resolve uma tentativa anterior que ficou sem desfecho conhecido.
    // Devolve a resposta pronta se a nota pode ser fechada; devolve null se a nota
    // foi liberada para uma nova tentativa.
    // ==================================================================
    private async Task<ImpressaoResposta?> ResolverPendenciaAsync(NotaFiscal nota, CancellationToken ct)
    {
        var chaveAnterior = nota.ChaveImpressao;

        // Estado inconsistente (Processando sem chave): nao ha o que perguntar, libera a nota.
        if (string.IsNullOrWhiteSpace(chaveAnterior))
        {
            await ReabrirAsync(nota, "A tentativa anterior nao registrou identificacao. Nota liberada.", ct);
            return null;
        }

        MovimentoRetorno? movimento;
        try
        {
            movimento = await estoque.ConsultarMovimentoAsync(chaveAnterior, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nota {Numero} presa: o Estoque ainda nao responde a consulta.", nota.Numero);

            throw ErroDeNegocioException.Indisponivel(
                $"A nota {nota.Numero} tem uma impressao pendente e o servico de Estoque continua indisponivel. " +
                "Assim que ele voltar, tente imprimir de novo (ou aguarde a regularizacao automatica).",
                new { notaId = nota.Id, numero = nota.Numero, statusDaNota = nota.Status.ToString() });
        }

        if (movimento is not null)
        {
            log.LogInformation("Nota {Numero}: a baixa pendente {Chave} tinha acontecido. Fechando.",
                nota.Numero, chaveAnterior);
            return await FecharAsync(nota, movimento, ct);
        }

        log.LogInformation("Nota {Numero}: a baixa pendente {Chave} nunca aconteceu. Liberando para nova tentativa.",
            nota.Numero, chaveAnterior);

        await ReabrirAsync(nota, null, ct);
        return null;
    }

    private async Task ReabrirAsync(NotaFiscal nota, string? motivo, CancellationToken ct)
    {
        nota.Status = StatusNota.Aberta;
        nota.ChaveImpressao = null;
        nota.UltimoErro = motivo is null ? null : Truncar(motivo);
        nota.AtualizadaEm = DateTime.UtcNow;
        await SalvarComControleDeVersaoAsync(nota, ct);
    }

    // ==================================================================
    // Desfecho A: deu certo
    // ==================================================================
    private async Task<ImpressaoResposta> FecharAsync(
        NotaFiscal nota, MovimentoRetorno movimento, CancellationToken ct)
    {
        nota.Status = StatusNota.Fechada;
        nota.ImpressaEm = DateTime.UtcNow;
        nota.AtualizadaEm = DateTime.UtcNow;
        nota.UltimoErro = null;
        await SalvarComControleDeVersaoAsync(nota, ct);

        log.LogInformation("Nota {Numero} impressa. Saldos: {Saldos}",
            nota.Numero,
            string.Join(", ", movimento.Itens.Select(i => $"{i.Codigo}={i.SaldoResultante}")));

        return new ImpressaoResposta(
            NotaFiscalServico.Mapear(nota), MapearSaldos(movimento), movimento.Repetido);
    }

    // ==================================================================
    // Desfecho B: o Estoque respondeu "nao" (saldo insuficiente, produto inexistente).
    // Nada foi baixado, entao e seguro devolver a nota para Aberta.
    // ==================================================================
    private async Task<ImpressaoResposta> ReabrirPorRecusaAsync(
        NotaFiscal nota, ResultadoEstoque.RecusadoPeloNegocio recusa, CancellationToken ct)
    {
        nota.Status = StatusNota.Aberta;
        nota.ChaveImpressao = null;
        nota.UltimoErro = Truncar(recusa.Mensagem);
        nota.AtualizadaEm = DateTime.UtcNow;
        await SalvarComControleDeVersaoAsync(nota, ct);

        log.LogWarning("Nota {Numero} nao pode ser impressa: {Codigo}.", nota.Numero, recusa.Codigo);

        throw new ErroDeNegocioException(
            recusa.Codigo,
            recusa.Mensagem,
            StatusCodes.Status409Conflict,
            new
            {
                notaId = nota.Id,
                numero = nota.Numero,
                statusDaNota = nota.Status.ToString(),
                origem = "Estoque",
                // Nome proprio em vez de "detalhes" de novo: senao a resposta sai com
                // detalhes.detalhes, e fica confuso saber de quem e cada pedaco.
                detalhesDoEstoque = recusa.Detalhes
            });
    }

    // ==================================================================
    // Desfecho C: nao sabemos o que aconteceu (timeout, servico fora, disjuntor aberto).
    // Regra de ouro: NAO ADIVINHAR. Perguntar.
    // ==================================================================
    private async Task<ImpressaoResposta> ResolverIncertezaAsync(
        NotaFiscal nota, string chave, ResultadoEstoque.Indisponivel falha, CancellationToken ct)
    {
        MovimentoRetorno? movimento = null;
        var conseguiuPerguntar = false;

        try
        {
            movimento = await estoque.ConsultarMovimentoAsync(chave, ct);
            conseguiuPerguntar = true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Tambem nao consegui perguntar ao Estoque sobre a chave {Chave}.", chave);
        }

        // C1: a baixa TINHA acontecido. So a resposta se perdeu. Fecha a nota normalmente.
        if (movimento is not null)
        {
            log.LogInformation("A baixa da nota {Numero} tinha acontecido; so a resposta se perdeu.", nota.Numero);
            return await FecharAsync(nota, movimento, ct);
        }

        // C2: perguntamos e a baixa NAO aconteceu. Nada foi alterado, e seguro reabrir.
        if (conseguiuPerguntar)
        {
            nota.Status = StatusNota.Aberta;
            nota.ChaveImpressao = null;
            nota.UltimoErro = Truncar(falha.Motivo);
            nota.AtualizadaEm = DateTime.UtcNow;
            await SalvarComControleDeVersaoAsync(nota, ct);

            throw ErroDeNegocioException.Indisponivel(
                $"{falha.Motivo} Nenhum saldo foi alterado e a nota {nota.Numero} continua Aberta. Tente novamente.",
                new { notaId = nota.Id, numero = nota.Numero, statusDaNota = nota.Status.ToString() });
        }

        // C3: nem perguntar deu. A nota FICA em Processando de proposito.
        // O reconciliador em segundo plano vai insistir ate descobrir o desfecho.
        nota.UltimoErro = Truncar(falha.Motivo);
        nota.AtualizadaEm = DateTime.UtcNow;
        await SalvarComControleDeVersaoAsync(nota, ct);

        log.LogError("Nota {Numero} ficou em Processando: nao foi possivel confirmar a baixa.", nota.Numero);

        throw ErroDeNegocioException.Indisponivel(
            $"{falha.Motivo} Nao foi possivel confirmar se o estoque foi baixado, entao a nota {nota.Numero} " +
            "ficou em processamento e sera regularizada automaticamente assim que o servico voltar.",
            new { notaId = nota.Id, numero = nota.Numero, statusDaNota = nota.Status.ToString() });
    }

    // ==================================================================
    private async Task SalvarComControleDeVersaoAsync(NotaFiscal nota, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Duas impressoes simultaneas da MESMA nota: uma delas perde aqui.
            throw ErroDeNegocioException.Conflito("NOTA_ALTERADA_EM_PARALELO",
                $"A nota {nota.Numero} esta sendo processada em outra janela. Recarregue a tela.",
                new { notaId = nota.Id, numero = nota.Numero });
        }
    }

    private static IReadOnlyList<SaldoAtualizado> MapearSaldos(MovimentoRetorno? movimento)
        => movimento is null
            ? []
            : movimento.Itens
                .Select(i => new SaldoAtualizado(i.ProdutoId, i.Codigo, i.Descricao, i.Quantidade, i.SaldoResultante))
                .ToList();

    private static string Truncar(string texto)
        => texto.Length <= 500 ? texto : texto[..497] + "...";
}
