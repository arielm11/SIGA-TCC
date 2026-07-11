using System.Net;
using System.Net.Http.Headers;
using Blazored.LocalStorage;
using TccManager.Client.Services;

namespace TccManager.Client.Handlers;

/// <summary>
/// Interceptor central de 401 (RF06 — docs/arquitetura/2026-07-10-refresh-token-sessao.md §6).
/// Anexa o bearer salvo no localStorage a toda requisição de saída (fonte única do header).
/// Em endpoints de auth (login/refresh/logout) apenas repassa — bypass, para não disparar
/// refresh a partir de um 401 de credencial inválida ou de um refresh já inválido. Em
/// qualquer outro 401, aciona o <see cref="ITokenRefreshCoordinator"/> (single-flight) e,
/// em caso de sucesso, reenvia a requisição original uma única vez com o novo bearer.
/// </summary>
public class AuthTokenHandler : DelegatingHandler
{
    private static readonly string[] RotasAuthSemRefresh =
    {
        "api/auth/login",
        "api/auth/refresh",
        "api/auth/logout"
    };

    private readonly ILocalStorageService _localStorage;
    private readonly ITokenRefreshCoordinator _refreshCoordinator;

    public AuthTokenHandler(ILocalStorageService localStorage, ITokenRefreshCoordinator refreshCoordinator)
    {
        _localStorage = localStorage;
        _refreshCoordinator = refreshCoordinator;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tokenUsado = await ObterTokenSalvoAsync(cancellationToken);
        AnexarBearer(request, tokenUsado);

        if (EhRotaAuth(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // P-C2: bufferiza o corpo ANTES do primeiro envio — um HttpContent já consumido
        // não pode ser reenviado no retry.
        var conteudoBufferizado = await BufferizarConteudoAsync(request, cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var refreshOk = await _refreshCoordinator.EnsureRefreshedAsync(tokenUsado ?? string.Empty);

        if (!refreshOk)
        {
            return response;
        }

        response.Dispose();

        var novoToken = await ObterTokenSalvoAsync(cancellationToken);

        var retryRequest = ClonarRequisicao(request, conteudoBufferizado);
        AnexarBearer(retryRequest, novoToken);

        // Retry único (§6.4): se o retry ainda vier 401, propaga como sessão encerrada —
        // não há nova tentativa de refresh a partir daqui.
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private async Task<string?> ObterTokenSalvoAsync(CancellationToken cancellationToken)
    {
        var token = await _localStorage.GetItemAsStringAsync("authToken", cancellationToken);
        return token?.Replace("\"", string.Empty);
    }

    private static void AnexarBearer(HttpRequestMessage request, string? token)
    {
        request.Headers.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    private static bool EhRotaAuth(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath.Trim('/').ToLowerInvariant() ?? string.Empty;
        return RotasAuthSemRefresh.Any(rota => path.EndsWith(rota, StringComparison.Ordinal));
    }

    private static async Task<byte[]?> BufferizarConteudoAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content == null)
            return null;

        return await request.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static HttpRequestMessage ClonarRequisicao(HttpRequestMessage original, byte[]? conteudoBufferizado)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (conteudoBufferizado != null && original.Content != null)
        {
            var novoConteudo = new ByteArrayContent(conteudoBufferizado);
            foreach (var header in original.Content.Headers)
            {
                novoConteudo.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = novoConteudo;
        }

        return clone;
    }
}
