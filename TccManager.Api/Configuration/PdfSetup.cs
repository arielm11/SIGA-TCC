using QuestPDF.Infrastructure;
using TccManager.Api.Services.Pdf;

namespace TccManager.Api.Configuration;

/// <summary>
/// Registro em DI do subsistema de geração de PDF da ata (N2 Etapa 1). Segue o mesmo
/// padrão de extensão estática já usado em EmailSetup. Config lida da seção "Ata"
/// (Instituicao, Curso) do appsettings.json.
/// </summary>
public static class PdfSetup
{
    public static IServiceCollection AddAtaPdf(this IServiceCollection services, IConfiguration configuration)
    {
        // Licença Community do QuestPDF — gratuita para projetos acadêmicos/sem
        // faturamento, confirmada em docs/requisitos/2026-07-13-pdf-ata-questpdf.md (RNF-01).
        QuestPDF.Settings.License = LicenseType.Community;

        services.Configure<AtaInstitucionalOptions>(configuration.GetSection("Ata"));

        services.AddScoped<IAtaPdfService, AtaPdfService>();

        return services;
    }
}
