using Ganss.Xss;

namespace TccManager.Api.Services;

/// <summary>
/// Implementação de <see cref="ISanitizerService"/> baseada no pacote Ganss.Xss (HtmlSanitizer).
/// Política: remove todas as tags HTML da entrada, preservando o texto interno como texto puro.
/// Registrada como Singleton no DI: o método Sanitize do HtmlSanitizer é thread-safe desde que a
/// configuração (allowlists) não seja alterada após a construção da instância, o que é o caso aqui
/// (configuração feita uma única vez no construtor e nunca mais alterada).
/// </summary>
public class HtmlSanitizerService : ISanitizerService
{
    private readonly HtmlSanitizer _sanitizer;

    public HtmlSanitizerService()
    {
        _sanitizer = new HtmlSanitizer();

        // Preserva o texto interno das tags removidas (produz texto puro em vez de descartar o conteúdo).
        _sanitizer.KeepChildNodes = true;

        // Nenhuma tag, atributo, propriedade CSS, at-rule ou esquema de URI é permitido: remove todo HTML.
        _sanitizer.AllowedTags.Clear();
        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowedSchemes.Clear();
    }

    public string? Sanitizar(string? entrada)
    {
        if (string.IsNullOrEmpty(entrada))
            return entrada;

        // Não decodificar o resultado: entidades HTML remanescentes (ex. "&lt;") representam
        // caracteres literais digitados pelo usuário e sua forma codificada é o que garante que
        // futuros consumidores (e-mail HTML, PDF) não reintroduzam HTML/JS executável.
        return _sanitizer.Sanitize(entrada);
    }
}
