using System.Net.Http.Json;
using Blazored.LocalStorage;
using TccManager.Client.Providers;
using TccManager.Shared.DTOs;

namespace TccManager.Client.Services;

public class TokenRefreshCoordinator : ITokenRefreshCoordinator
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalStorageService _localStorage;
    private readonly ISessionEndedHandler _sessionEndedHandler;
    private readonly CustomAuthStateProvider _authStateProvider;

    public TokenRefreshCoordinator(
        IHttpClientFactory httpClientFactory,
        ILocalStorageService localStorage,
        ISessionEndedHandler sessionEndedHandler,
        CustomAuthStateProvider authStateProvider)
    {
        _httpClientFactory = httpClientFactory;
        _localStorage = localStorage;
        _sessionEndedHandler = sessionEndedHandler;
        _authStateProvider = authStateProvider;
    }

    public async Task<bool> EnsureRefreshedAsync(string tokenUsado)
    {
        await _semaphore.WaitAsync();
        try
        {
            var tokenAtual = await ObterItemSemAspasAsync("authToken");

            // Outra requisição concorrente já renovou a sessão enquanto este chamador
            // esperava o lock — não chama o servidor de novo, apenas reaproveita.
            if (!string.IsNullOrEmpty(tokenAtual) && tokenAtual != tokenUsado)
            {
                return true;
            }

            var refreshTokenAtual = await ObterItemSemAspasAsync("refreshToken");

            if (string.IsNullOrWhiteSpace(refreshTokenAtual))
            {
                await _sessionEndedHandler.EncerrarSessaoAsync();
                return false;
            }

            var client = _httpClientFactory.CreateClient("AuthRaw");
            var response = await client.PostAsJsonAsync("api/auth/refresh", new RefreshRequestDto
            {
                RefreshToken = refreshTokenAtual
            });

            if (!response.IsSuccessStatusCode)
            {
                await _sessionEndedHandler.EncerrarSessaoAsync();
                return false;
            }

            var par = await response.Content.ReadFromJsonAsync<TokenPairDto>();

            if (par == null || string.IsNullOrWhiteSpace(par.Token) || string.IsNullOrWhiteSpace(par.RefreshToken))
            {
                await _sessionEndedHandler.EncerrarSessaoAsync();
                return false;
            }

            await _localStorage.SetItemAsync("authToken", par.Token);
            await _localStorage.SetItemAsync("refreshToken", par.RefreshToken);

            _authStateProvider.NotifyUserAuthentication(par.Token);

            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string?> ObterItemSemAspasAsync(string chave)
    {
        var valor = await _localStorage.GetItemAsStringAsync(chave);
        return valor?.Replace("\"", string.Empty);
    }
}
