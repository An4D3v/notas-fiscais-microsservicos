namespace NotasFiscais.Faturamento.Api.Clientes;

/// <summary>
/// Politica de resiliencia da conversa com o Estoque, em configuracao e nao no meio do codigo.
///
/// Isso existe por dois motivos praticos:
///   1. em producao da para afrouxar ou apertar sem recompilar;
///   2. os testes automatizados conseguem encurtar os tempos. Sem isso, o teste que
///      derruba o Estoque teria que esperar 10 segundos de disjuntor aberto para
///      provar que o servico volta, e uma suite lenta e uma suite que ninguem roda.
///
/// Os valores padrao sao os de producao.
/// </summary>
public class OpcoesResiliencia
{
    /// <summary>Tempo maximo de UMA tentativa. Curto: se engasgou, e melhor tentar de novo.</summary>
    public double TimeoutPorTentativaSegundos { get; set; } = 2;

    /// <summary>Teto do conjunto (tentativas + esperas). Precisa ser maior que a soma delas.</summary>
    public double TimeoutTotalSegundos { get; set; } = 12;

    public int MaxTentativas { get; set; } = 2;

    /// <summary>Espera antes da primeira retentativa. Dobra a cada tentativa, com variacao aleatoria.</summary>
    public int EsperaInicialMs { get; set; } = 400;

    /// <summary>Janela que o disjuntor observa para decidir se a coisa esta ruim.</summary>
    public double JanelaDisjuntorSegundos { get; set; } = 30;

    /// <summary>Proporcao de falhas que abre o disjuntor (0,5 = metade).</summary>
    public double ProporcaoFalhaDisjuntor { get; set; } = 0.5;

    /// <summary>Abaixo deste numero de chamadas na janela, o disjuntor nem considera abrir.</summary>
    public int MinimoChamadasDisjuntor { get; set; } = 4;

    /// <summary>Quanto tempo o disjuntor fica aberto antes de testar o servico de novo.</summary>
    public double DuracaoAberturaDisjuntorSegundos { get; set; } = 10;

    /// <summary>Timeout do cliente de consulta (o que NAO passa pelo disjuntor).</summary>
    public double TimeoutConsultaSegundos { get; set; } = 3;
}
