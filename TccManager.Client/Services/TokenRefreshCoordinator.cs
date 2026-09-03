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

            HttpResponseMessage? response = null;
            try
            {
                response = await client.PostAsJsonAsync("api/auth/refresh", new RefreshRequestDto
                {
                    RefreshToken = refreshTokenAtual
                });
            }
            catch (Exception)
            {
                // Falha de rede/transporte (issue #85, cenário B3): antes, isso escapava
                // como exceção não tratada até a página. Tratada abaixo como qualquer outra
                // falha — a releitura do localStorage decide se outra aba já resolveu.
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                return await AdotarSeOutraAbaJaRenovouAsync(refreshTokenAtual);
            }

            var par = await response.Content.ReadFromJsonAsync<TokenPairDto>();

            if (par == null || string.IsNullOrWhiteSpace(par.Token) || string.IsNullOrWhiteSpace(par.RefreshToken))
            {
                return await AdotarSeOutraAbaJaRenovouAsync(refreshTokenAtual);
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

    /// <summary>
    /// Rede de segurança para falha de <c>/refresh</c> (exceção de transporte ou resposta
    /// não-2xx/malformada) — issue #85, D10. Relê o <c>refreshToken</c> do <c>localStorage</c>
    /// (compartilhado entre abas): se ele mudou desde que esta chamada foi enviada, outra aba
    /// já completou a rotação nesse meio-tempo — adota o par que ela gravou em vez de encerrar
    /// a sessão por um "401 benigno". Só encerra a sessão se, após a releitura, o par
    /// continuar sendo o mesmo que falhou. Complementa a janela de graça do servidor (D1);
    /// não a substitui — a releitura pode acontecer antes de a aba vencedora gravar o par novo.
    /// </summary>
    private async Task<bool> AdotarSeOutraAbaJaRenovouAsync(string? refreshTokenEnviado)
    {
        var refreshTokenReleitura = await ObterItemSemAspasAsync("refreshToken");

        if (!string.IsNullOrEmpty(refreshTokenReleitura) && refreshTokenReleitura != refreshTokenEnviado)
        {
            var authTokenReleitura = await ObterItemSemAspasAsync("authToken");
            if (!string.IsNullOrEmpty(authTokenReleitura))
            {
                _authStateProvider.NotifyUserAuthentication(authTokenReleitura);
            }

            return true;
        }

        await _sessionEndedHandler.EncerrarSessaoAsync();
        return false;
    }

    private async Task<string?> ObterItemSemAspasAsync(string chave)
    {
        var valor = await _localStorage.GetItemAsStringAsync(chave);
        return valor?.Replace("\"", string.Empty);
    }
}
