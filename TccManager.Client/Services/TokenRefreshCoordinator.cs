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

    /// <summary>
    /// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, §6.2) — o
    /// refresh token deixou de existir no localStorage (passa a viver só no cookie HttpOnly
    /// <c>siga_refresh</c>, ilegível por JS). O sinal de "outra aba/requisição já renovou"
    /// passa a ser o próprio <c>authToken</c>, comparado antes e depois da chamada de rede.
    /// </summary>
    public async Task<bool> EnsureRefreshedAsync(string tokenUsado)
    {
        await _semaphore.WaitAsync();
        try
        {
            var authTokenAntes = await ObterItemSemAspasAsync("authToken");

            // Outra requisição concorrente (nesta aba ou em outra, via localStorage
            // compartilhado) já renovou a sessão enquanto este chamador esperava o lock ou
            // navegava — não chama o servidor de novo, apenas reaproveita.
            if (!string.IsNullOrEmpty(authTokenAntes) && authTokenAntes != tokenUsado)
            {
                return true;
            }

            // Sem authToken salvo: não há sessão local para renovar (D8 — nunca reativa uma
            // sessão encerrada chamando /refresh "a frio", só com um cookie que pode nem
            // existir mais).
            if (string.IsNullOrEmpty(authTokenAntes))
            {
                await _sessionEndedHandler.EncerrarSessaoAsync();
                return false;
            }

            var client = _httpClientFactory.CreateClient("AuthRaw");

            HttpResponseMessage? response = null;
            RefreshResponseDto? resultado = null;
            try
            {
                // Sem corpo: o refresh token vai pelo cookie HttpOnly, anexado
                // automaticamente pelo navegador (BrowserCredentialsHandler marca a
                // requisição com credenciais "include").
                response = await client.PostAsync("api/auth/refresh", null);

                if (response.IsSuccessStatusCode)
                {
                    // Achado L4 da revisão de segurança (#171): a desserialização precisa
                    // estar dentro deste try. Uma resposta 2xx com corpo que não é JSON
                    // válido (proxy devolvendo HTML, corpo truncado, erro de rede no meio da
                    // leitura) lança JsonException/HttpRequestException — se isso acontecesse
                    // fora daqui, subiria sem tratamento até o AuthTokenHandler/
                    // CustomAuthStateProvider em vez de cair no mesmo caminho de recuperação
                    // (AdotarSeOutraAbaJaRenovouAsync) que falha de transporte já usa.
                    resultado = await response.Content.ReadFromJsonAsync<RefreshResponseDto>();
                }
            }
            catch (Exception)
            {
                // Falha de rede/transporte (issue #85, cenário B3) ou corpo 2xx malformado
                // (achado L4 acima): tratadas abaixo como qualquer outra falha — a releitura
                // do localStorage decide se outra aba já resolveu.
                response = null;
                resultado = null;
            }

            if (response == null || !response.IsSuccessStatusCode || resultado == null || string.IsNullOrWhiteSpace(resultado.Token))
            {
                return await AdotarSeOutraAbaJaRenovouAsync(authTokenAntes);
            }

            // Nunca grava refreshToken — o sucessor do cookie já foi gravado pelo navegador a
            // partir do Set-Cookie da própria resposta (D1/D2).
            await _localStorage.SetItemAsync("authToken", resultado.Token);

            _authStateProvider.NotifyUserAuthentication(resultado.Token);

            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Rede de segurança para falha de <c>/refresh</c> (exceção de transporte, resposta
    /// não-2xx ou corpo malformado/sem <c>Token</c>) — issue #85, §6.2/6.3 da arquitetura.
    /// Relê o <c>authToken</c> do <c>localStorage</c> (compartilhado entre abas): se ele mudou
    /// desde a leitura feita antes do envio, outra aba (ou chamada concorrente) já completou
    /// uma renovação ou um login nesse meio-tempo — adota, em vez de encerrar a sessão por um
    /// "401 benigno". Só encerra se, após a releitura, o valor continuar o mesmo que falhou.
    /// </summary>
    private async Task<bool> AdotarSeOutraAbaJaRenovouAsync(string? authTokenAntes)
    {
        var authTokenReleitura = await ObterItemSemAspasAsync("authToken");

        if (!string.IsNullOrEmpty(authTokenReleitura) && authTokenReleitura != authTokenAntes)
        {
            _authStateProvider.NotifyUserAuthentication(authTokenReleitura);
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
