namespace NotasFiscais.Faturamento.Api.Servicos.IA;

public class OpcoesIA
{
    /// <summary>
    /// Chave da API do Google Gemini. Fica FORA do appsettings de proposito:
    /// vem da variavel de ambiente GEMINI_API_KEY ou dos user-secrets do dotnet.
    /// Sem chave, o sistema cai no modo offline e continua funcionando.
    /// </summary>
    public string? ApiKey { get; set; }

    public string Modelo { get; set; } = "gemini-flash-lite-latest";

    public string UrlBase { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>Quantos produtos vao no prompt. Limita o tamanho (e o custo) da chamada.</summary>
    public int MaxProdutosNoPrompt { get; set; } = 200;

    /// <summary>Tempo maximo de espera pelo modelo. Curto: a IA e um atalho, nao pode travar a tela.</summary>
    public int TimeoutSegundos { get; set; } = 20;

    public bool Habilitada => !string.IsNullOrWhiteSpace(ApiKey);
}
