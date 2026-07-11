using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace TccManager.Api.Services.Notifications;

/// <summary>
/// Carrega os 7 templates .html de Resources/EmailTemplates (embutidos como embedded
/// resource no .csproj) e substitui placeholders "{{Chave}}". Cache em memória por chave
/// evita reler o assembly a cada envio (I/O ocorre só uma vez por template, na primeira vez).
/// </summary>
public class FileEmailTemplateRenderer : IEmailTemplateRenderer
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public string Render(string chaveTemplate, IReadOnlyDictionary<string, string> valores)
    {
        var template = Cache.GetOrAdd(chaveTemplate, CarregarTemplate);

        var corpo = template;
        foreach (var (chave, valor) in valores)
        {
            corpo = corpo.Replace($"{{{{{chave}}}}}", valor ?? string.Empty);
        }

        return corpo;
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
