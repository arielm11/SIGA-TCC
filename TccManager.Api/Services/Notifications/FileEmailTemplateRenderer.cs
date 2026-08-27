using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace TccManager.Api.Services.Notifications;

/// <summary>
/// Carrega os 7 templates .html de Resources/EmailTemplates (embutidos como embedded
/// resource no .csproj) e substitui placeholders "{{Chave}}". Cache em memória por chave
/// evita reler o assembly a cada envio (I/O ocorre só uma vez por template, na primeira vez).
/// </summary>
public class FileEmailTemplateRenderer : IEmailTemplateRenderer
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    private static readonly Regex PlaceholderRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    private readonly ILogger<FileEmailTemplateRenderer> _logger;

    public FileEmailTemplateRenderer(ILogger<FileEmailTemplateRenderer> logger)
    {
        _logger = logger;
    }

    public string Render(string chaveTemplate, IReadOnlyDictionary<string, string> valores)
    {
        var template = Cache.GetOrAdd(chaveTemplate, CarregarTemplate);

        // Substituição em uma única passada (Regex.Replace + MatchEvaluator), não N chamadas
        // sequenciais de string.Replace: se o valor de um placeholder contivesse a sintaxe
        // "{{OutraChave}}", uma substituição posterior o pegaria de novo (dupla substituição).
        // Placeholder sem entrada em "valores" permanece intacto no corpo (mesmo comportamento
        // anterior, que só substituía as chaves fornecidas) — mas agora gera um Warning, para
        // que um template editado sem atualizar o código que o preenche não vaze "{{Chave}}"
        // cru para o destinatário em silêncio.
        return PlaceholderRegex.Replace(template, match =>
        {
            var chave = match.Groups[1].Value;

            if (valores.TryGetValue(chave, out var valor))
                return valor ?? string.Empty;

            _logger.LogWarning(
                "Placeholder {Chave} do template {ChaveTemplate} não foi fornecido; permanecerá literal no corpo do e-mail.",
                chave,
                chaveTemplate);
            return match.Value;
        });
    }

    private static string CarregarTemplate(string chaveTemplate)
    {
        var assembly = typeof(FileEmailTemplateRenderer).Assembly;
        var resourceName = $"TccManager.Api.Resources.EmailTemplates.{chaveTemplate}.html";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Template de e-mail não encontrado: {resourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
