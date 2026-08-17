using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #60 — política de rate limiting "refresh", criada para <c>/api/auth/refresh</c>.
/// Antes desta correção o endpoint compartilhava a política "login" (5 req/janela), o que
/// fazia a renovação silenciosa do cliente competir pelo mesmo orçamento das tentativas de
/// login e, na prática, derrubar sessões legítimas.
///
/// Valores esperados vêm do appsettings.json versionado da API
/// (<c>RateLimiting:Refresh</c> = 15 req / 60 s / QueueLimit 0), no mesmo padrão já adotado
/// por <see cref="RateLimitingLoginTests"/>.
///
/// Isolamento: cada teste cria sua própria <see cref="TccApiFactory"/>, portanto sua própria
/// instância do PartitionedRateLimiter — a contagem não vaza entre casos.
/// </summary>
public class RateLimitingRefreshTests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";

    private const int LimiteRefresh = 15;
    private const int LimiteLogin = 5;

    private static RefreshRequestDto TokenInvalido() =>
        new() { RefreshToken = Guid.NewGuid().ToString() };

    private static LoginDto CredencialInvalida() =>
        new() { Email = "naoexiste@teste.com", Senha = "errada" };

    [Fact]
    public async Task DentroDoLimite_QuinzeChamadas_NenhumaEhBloqueada()
    {
        // No código anterior (política "login" compartilhada) a 6ª chamada já viria 429.
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            var resp = await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [Fact]
    public async Task DecimaSextaChamadaDentroDaJanela_Retorna429ComRetryAfter()
    {
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            var permitida = await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        var bloqueada = await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 do /refresh deve conter o header Retry-After, como a de /login.");

        var retryAfter = bloqueada.Headers.GetValues("Retry-After").First();
        Assert.True(int.TryParse(retryAfter, out var segundos),
            $"Retry-After deveria ser um inteiro em segundos, mas foi '{retryAfter}'.");
        Assert.InRange(segundos, 1, 60);
    }

    [Fact]
    public async Task LimiteDeLoginEsgotado_NaoBloqueiaORefresh()
    {
        // Núcleo da correção: as duas rotas passaram a ter orçamentos independentes.
        // No código anterior, esgotar o /login também esgotava o /refresh.
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= LimiteLogin; i++)
        {
            await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        }

        var loginBloqueado = await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        Assert.Equal(HttpStatusCode.TooManyRequests, loginBloqueado.StatusCode);

        var refresh = await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task LimiteDeRefreshEsgotado_NaoBloqueiaOLogin()
    {
        // Recíproca: o inverso também precisa valer, senão um cliente com renovação em
        // loop impediria o usuário de refazer login manualmente.
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());
        }

        var refreshBloqueado = await client.PostAsJsonAsync(RotaRefresh, TokenInvalido());
        Assert.Equal(HttpStatusCode.TooManyRequests, refreshBloqueado.StatusCode);

        var login = await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task LogoutContinuaNaPoliticaLogin_ConsumindoOMesmoOrcamentoDoLogin()
    {
        // A issue #60 moveu apenas o /refresh: /login e /logout seguem em "login".
        // Este teste trava esse contorno para que uma mudança futura seja deliberada.
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= LimiteLogin; i++)
        {
            await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        }

        var logout = await client.PostAsJsonAsync("/api/auth/logout",
            new LogoutRequestDto { RefreshToken = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.TooManyRequests, logout.StatusCode);
    }
}
