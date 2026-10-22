using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NotasFiscais.Estoque.Api.Dados;

/// <summary>
/// Conserta um problema classico e silencioso: o SQL Server guarda a data,
/// mas NAO guarda o fuso. Quando o EF le a coluna de volta, a data chega com
/// Kind = Unspecified. Ao virar JSON, ela sai sem o "Z" do fim, e o navegador
/// entende como HORA LOCAL.
///
/// O sintoma e cruel porque e intermitente: a mesma data aparece certa quando
/// vem da memoria (logo depois de gravar) e errada quando vem do banco.
/// Deu para ver na tela: "criada em 21:45, impressa em 18:45", na mesma nota.
///
/// A regra aqui: tudo e gravado em UTC e tudo volta marcado como UTC.
/// A conversao para o fuso de quem esta olhando e responsabilidade da tela.
/// </summary>
public class DataUtcConverter() : ValueConverter<DateTime, DateTime>(
    aoGravar => aoGravar.Kind == DateTimeKind.Local ? aoGravar.ToUniversalTime() : aoGravar,
    aoLer => DateTime.SpecifyKind(aoLer, DateTimeKind.Utc));

public class DataUtcNulavelConverter() : ValueConverter<DateTime?, DateTime?>(
    aoGravar => aoGravar.HasValue && aoGravar.Value.Kind == DateTimeKind.Local
        ? aoGravar.Value.ToUniversalTime()
        : aoGravar,
    aoLer => aoLer.HasValue
        ? DateTime.SpecifyKind(aoLer.Value, DateTimeKind.Utc)
        : aoLer);
