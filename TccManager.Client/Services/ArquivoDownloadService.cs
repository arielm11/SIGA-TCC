using Microsoft.JSInterop;

namespace TccManager.Client.Services;

public class ArquivoDownloadService : IArquivoDownloadService
{
    private readonly IJSRuntime _jsRuntime;

    public ArquivoDownloadService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<HttpResponseMessage> BaixarAsync(HttpClient http, string url, string nomeArquivo, string mimeType)
    {
        var response = await http.GetAsync(url);

        if (response.IsSuccessStatusCode)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var base64 = Convert.ToBase64String(bytes);
            await _jsRuntime.InvokeVoidAsync("downloadFileFromBytes", nomeArquivo, mimeType, base64);
        }

        return response;
    }
}
