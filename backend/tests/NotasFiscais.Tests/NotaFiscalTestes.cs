using System.Net;
using System.Net.Http.Json;

namespace NotasFiscais.Tests;

[Collection("ambiente")]
public class NotaFiscalTestes(AmbienteDeTeste ambiente)
{
    // ==================================================================
    // Numeracao
    // ==================================================================

    [Fact]
    public async Task Notas_recebem_numeracao_sequencial()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 50);

        var primeira = await ambiente.CriarNotaAsync((produto, 1));
        var segunda = await ambiente.CriarNotaAsync((produto, 1));
        var terceira = await ambiente.CriarNotaAsync((produto, 1));

        Assert.Equal(primeira.Numero + 1, segunda.Numero);
        Assert.Equal(segunda.Numero + 1, terceira.Numero);
        Assert.Equal("Aberta", primeira.Status);
    }

    [Fact]
    public async Task Notas_criadas_ao_mesmo_tempo_nao_repetem_numero()
    {
        // Aqui esta a razao de a numeracao NAO usar MAX(Numero) + 1:
        // sob concorrencia, duas notas leriam o mesmo maximo e receberiam o mesmo numero.
        // A tabela de sequencia trava a linha do contador ate o commit, formando fila.
        var produto = await ambiente.CriarProdutoAsync(saldo: 100);

        var notas = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => ambiente.CriarNotaAsync((produto, 1))));

        var numeros = notas.Select(n => n.Numero).OrderBy(n => n).ToList();

        Assert.Equal(10, numeros.Distinct().Count());

        // Sem buraco: do menor ao maior, de um em um.
        Assert.Equal(Enumerable.Range(numeros[0], 10), numeros);
    }

    [Fact]
    public async Task Nota_nasce_aberta_com_os_itens_informados()
    {
        var caneta = await ambiente.CriarProdutoAsync(saldo: 20);
        var caderno = await ambiente.CriarProdutoAsync(saldo: 20);

        var nota = await ambiente.CriarNotaAsync((caneta, 3), (caderno, 2));

        Assert.Equal("Aberta", nota.Status);
        Assert.Equal(2, nota.Itens.Count);
        Assert.Equal(3, nota.Itens.Single(i => i.ProdutoId == caneta.Id).Quantidade);
        Assert.Null(nota.ImpressaEm);
    }

    [Fact]
    public async Task Produto_repetido_na_mesma_nota_soma_a_quantidade()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 20);

        var nota = await ambiente.CriarNotaAsync((produto, 2), (produto, 3));

        var item = Assert.Single(nota.Itens);
        Assert.Equal(5, item.Quantidade);
    }

    [Fact]
    public async Task Nota_sem_item_e_recusada()
    {
        var resposta = await ambiente.Faturamento.PostAsJsonAsync("/notas", new { itens = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("DADOS_INVALIDOS", await AmbienteDeTeste.CodigoDoErroAsync(resposta));
    }

    // ==================================================================
    // Impressao
    // ==================================================================

    [Fact]
    public async Task Imprimir_fecha_a_nota_e_baixa_o_saldo()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 10);
        var nota = await ambiente.CriarNotaAsync((produto, 2));

        var resposta = await ambiente.ImprimirAsync(nota.Id, $"imp-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var resultado = await resposta.Content.ReadFromJsonAsync<ImpressaoDto>(AmbienteDeTeste.Json);

        Assert.Equal("Fechada", resultado!.Nota.Status);
        Assert.NotNull(resultado.Nota.ImpressaEm);
        Assert.Equal(8, resultado.SaldosAtualizados.Single().SaldoResultante);
        Assert.Equal(8, await ambiente.SaldoAsync(produto.Id));
    }

    [Fact]
    public async Task Nota_ja_impressa_nao_imprime_de_novo()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 10);
        var nota = await ambiente.CriarNotaAsync((produto, 1));

        await ambiente.ImprimirAsync(nota.Id, $"imp-{Guid.NewGuid()}");
        var segunda = await ambiente.ImprimirAsync(nota.Id, $"outra-chave-{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Equal("NOTA_JA_IMPRESSA", await AmbienteDeTeste.CodigoDoErroAsync(segunda));

        // Saiu 1 unidade, e nao 2.
        Assert.Equal(9, await ambiente.SaldoAsync(produto.Id));
    }

    [Fact]
    public async Task Duplo_clique_no_botao_imprimir_nao_baixa_o_saldo_duas_vezes()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 10);
        var nota = await ambiente.CriarNotaAsync((produto, 3));
        var chave = $"duplo-{Guid.NewGuid()}";

        var primeira = await ambiente.ImprimirAsync(nota.Id, chave);
        var segunda = await ambiente.ImprimirAsync(nota.Id, chave);

        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);

        var r1 = await primeira.Content.ReadFromJsonAsync<ImpressaoDto>(AmbienteDeTeste.Json);
        var r2 = await segunda.Content.ReadFromJsonAsync<ImpressaoDto>(AmbienteDeTeste.Json);

        Assert.False(r1!.Repetido);
        Assert.True(r2!.Repetido);
        Assert.Equal("Fechada", r2.Nota.Status);

        Assert.Equal(7, await ambiente.SaldoAsync(produto.Id));
    }

    [Fact]
    public async Task Impressao_sem_saldo_deixa_a_nota_aberta_com_o_motivo()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 2);
        var nota = await ambiente.CriarNotaAsync((produto, 5));

        var resposta = await ambiente.ImprimirAsync(nota.Id, $"sem-saldo-{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("SALDO_INSUFICIENTE", await AmbienteDeTeste.CodigoDoErroAsync(resposta));

        // A nota volta para Aberta (nao fica presa) e guarda o motivo para a tela mostrar.
        var depois = await ambiente.Faturamento.GetFromJsonAsync<NotaDto>($"/notas/{nota.Id}", AmbienteDeTeste.Json);
        Assert.Equal("Aberta", depois!.Status);
        Assert.Contains("Saldo insuficiente", depois.UltimoErro);

        Assert.Equal(2, await ambiente.SaldoAsync(produto.Id));
    }

    [Fact]
    public async Task Duas_notas_disputando_a_ultima_unidade_so_uma_imprime()
    {
        // O cenario de concorrencia visto de ponta a ponta, passando pelos DOIS servicos.
        var produto = await ambiente.CriarProdutoAsync(saldo: 1);
        var notaA = await ambiente.CriarNotaAsync((produto, 1));
        var notaB = await ambiente.CriarNotaAsync((produto, 1));

        var respostas = await Task.WhenAll(
            ambiente.ImprimirAsync(notaA.Id, $"a-{Guid.NewGuid()}"),
            ambiente.ImprimirAsync(notaB.Id, $"b-{Guid.NewGuid()}"));

        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, await ambiente.SaldoAsync(produto.Id));

        var a = await ambiente.Faturamento.GetFromJsonAsync<NotaDto>($"/notas/{notaA.Id}", AmbienteDeTeste.Json);
        var b = await ambiente.Faturamento.GetFromJsonAsync<NotaDto>($"/notas/{notaB.Id}", AmbienteDeTeste.Json);

        var status = new[] { a!.Status, b!.Status }.OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Aberta", "Fechada" }, status);
    }

    [Fact]
    public async Task Nota_fechada_nao_pode_ser_excluida()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 5);
        var nota = await ambiente.CriarNotaAsync((produto, 1));
        await ambiente.ImprimirAsync(nota.Id, $"imp-{Guid.NewGuid()}");

        var resposta = await ambiente.Faturamento.DeleteAsync($"/notas/{nota.Id}");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("NOTA_FECHADA", await AmbienteDeTeste.CodigoDoErroAsync(resposta));
    }

    [Fact]
    public async Task Nota_aberta_aceita_novo_item_e_nota_fechada_nao()
    {
        var produto = await ambiente.CriarProdutoAsync(saldo: 20);
        var extra = await ambiente.CriarProdutoAsync(saldo: 20);
        var nota = await ambiente.CriarNotaAsync((produto, 1));

        var itens = new
        {
            itens = new[]
            {
                new { produtoId = extra.Id, codigo = extra.Codigo, descricao = extra.Descricao, quantidade = 2 },
            },
        };

        var adicionar = await ambiente.Faturamento.PostAsJsonAsync($"/notas/{nota.Id}/itens", itens);
        Assert.Equal(HttpStatusCode.OK, adicionar.StatusCode);

        await ambiente.ImprimirAsync(nota.Id, $"imp-{Guid.NewGuid()}");

        var depoisDeFechar = await ambiente.Faturamento.PostAsJsonAsync($"/notas/{nota.Id}/itens", itens);
        Assert.Equal(HttpStatusCode.Conflict, depoisDeFechar.StatusCode);
        Assert.Equal("NOTA_NAO_ESTA_ABERTA", await AmbienteDeTeste.CodigoDoErroAsync(depoisDeFechar));
    }
}
