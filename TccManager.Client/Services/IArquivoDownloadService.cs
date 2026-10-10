namespace TccManager.Client.Services;

/// <summary>
/// Issue #155 (achado B7): sequência GetAsync → IsSuccessStatusCode → ReadAsByteArrayAsync →
/// Convert.ToBase64String → JSRuntime.InvokeVoidAsync("downloadFileFromBytes", ...) estava
/// duplicada 7x em 5 arquivos. Encapsula só o mecanismo de download (a "via Base64 + blob");
/// cada chamador continua decidindo como tratar a resposta quando <c>IsSuccessStatusCode</c>
/// é falso (mensagem de erro, switch por StatusCode, etc. — issue #157 trata a inconsistência
/// entre esses tratamentos, não esta issue).
/// </summary>
public interface IArquivoDownloadService
{
    /// <summary>
    /// Faz o GET autenticado em <paramref name="url"/> e, se a resposta for de sucesso,
    /// dispara o download do arquivo via <c>downloadFileFromBytes</c>. Retorna a resposta
    /// HTTP sempre (mesmo em erro) para o chamador decidir como tratar a falha.
    /// </summary>
    Task<HttpResponseMessage> BaixarAsync(HttpClient http, string url, string nomeArquivo, string mimeType);
}
