namespace TccManager.Api.Services.Notifications;

/// <summary>
/// Isola a estratégia de renderização de template (hoje: arquivo .html embutido como
/// embedded resource + substituição de placeholders {{Chave}}), permitindo trocar por
/// Razor no futuro sem afetar TccNotificationService.
/// </summary>
public interface IEmailTemplateRenderer
{
    /// <summary>
    /// Renderiza o template identificado por <paramref name="chaveTemplate"/> (nome do
    /// arquivo .html, sem extensão, ex.: "proposta-aprovada"), substituindo cada
    /// ocorrência de "{{Chave}}" pelo valor correspondente em <paramref name="valores"/>.
    /// Os valores já devem vir prontos para inserção no HTML (texto plano ou fragmento
    /// HTML controlado) — o renderer não faz encoding adicional.
    /// </summary>
    string Render(string chaveTemplate, IReadOnlyDictionary<string, string> valores);
}
