using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, seção 9 — "Novo —
/// contrato do cookie" / "Novo — D6" / "Novo — logout" / "Novo — RF12"). Contrato do cookie
/// "siga_refresh" em si — atributos, D6 (401 de /refresh nunca expira o cookie) e RF04/RF12 —
/// coberto separadamente da lógica de rotação/sessão (<c>AuthController_RefreshToken_Tests</c> e
/// correlatos), que não precisa repetir estas asserções em cada cenário.
/// </summary>
public class AuthController_RefreshTokenCookie_Tests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";
    private const string RotaLogout = "/api/auth/logout";
    private const string RotaTrocarSenhaObrigatoria = "/api/auth/trocar-senha-obrigatoria";
    private const string SenhaValida = "SenhaValida123";
    private const string EmailPadrao = "cookie@teste.com";

    private static async Task SemearUsuarioAsync(TccApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();
        context.Usuarios.Add(new Usuario
        {
            Nome = "Usuario Cookie",
            Email = EmailPadrao,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        await context.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> FazerLoginAsync(HttpClient client) =>
        client.PostAsJsonAsync(RotaLogin, new LoginDto { Email = EmailPadrao, Senha = SenhaValida });

    private static Task<HttpResponseMessage> RefreshComCookieAsync(HttpClient client, string? refreshToken) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, refreshToken));

    private static Task<HttpResponseMessage> LogoutComCookieAsync(HttpClient client, string? refreshToken) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaLogout, refreshToken));

    // ══════════════ Contrato de atributos (RF07/D2) ══════════════

    [Fact]
    public async Task Login_Development_GravaCookieComAtributosEsperadosESemSecure()
    {
        // TccApiFactory sobe em "Development" — Secure=false é a decisão 5.3 para esse ambiente.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resposta = await FazerLoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var setCookie = CookieTestHelpers.ExtrairSetCookie(resposta);
        Assert.NotNull(setCookie);

        Assert.Equal(string.Empty, CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "HttpOnly"));
        Assert.Equal("strict", CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "SameSite"));
        Assert.Equal("/api/auth", CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Path"));
        Assert.Null(CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Domain"));
        // Development: Secure=false — o atributo "secure" não aparece no Set-Cookie.
        Assert.Null(CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Secure"));

        var maxAgeBruto = CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Max-Age");
        Assert.NotNull(maxAgeBruto);
        var maxAge = int.Parse(maxAgeBruto!);
        // Jwt:RefreshTokenDays padrão (appsettings.json) = 7 dias. Tolerância generosa para o
        // tempo de execução do teste em si.
        Assert.InRange(maxAge, 7 * 86400 - 30, 7 * 86400);
    }

    [Fact]
    public async Task Login_ForaDeDevelopment_CookieTemSecure()
    {
        using var factory = new ProductionApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resposta = await FazerLoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        Assert.Equal(string.Empty, CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Secure"));
        Assert.Equal(string.Empty, CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "HttpOnly"));
        Assert.Equal("strict", CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "SameSite"));

        // Achado L3: fora de Development o cookie usa o prefixo __Host- e Path=/ (a
        // especificação proíbe __Host- com qualquer Path que não seja a raiz).
        var setCookie = CookieTestHelpers.ExtrairSetCookie(resposta);
        Assert.NotNull(setCookie);
        Assert.StartsWith(
            $"{CookieTestHelpers.NomeCookieRefreshForaDeDevelopment}=", setCookie, StringComparison.Ordinal);
        Assert.Equal("/", CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Path"));
        Assert.Null(CookieTestHelpers.ExtrairAtributoCookieRefresh(resposta, "Domain"));
    }

    [Fact]
    public async Task Login_Development_CookieUsaNomeSemPrefixoHost()
    {
        // Contraparte do teste acima: em Development o nome permanece "siga_refresh" (sem
        // prefixo), porque __Host- exige Secure=true e a decisão 5.3 mantém Secure=false em dev.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resposta = await FazerLoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var setCookie = CookieTestHelpers.ExtrairSetCookie(resposta);
        Assert.NotNull(setCookie);
        Assert.StartsWith($"{CookieTestHelpers.NomeCookieRefresh}=", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("__Host-", setCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForaDeDevelopment_LoginRefreshLogout_NomeEPathNuncaDivergem()
    {
        // D3/D7 estendidos pelo achado L3: gravar (login/refresh) e expirar (logout) têm que usar
        // exatamente o mesmo nome ("__Host-siga_refresh") e o mesmo Path ("/") fora de
        // Development — senão o navegador mascara em vez de remover o cookie (RF04).
        using var factory = new ProductionApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var login = await FazerLoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var nomeCookie = CookieTestHelpers.NomeCookieRefreshForaDeDevelopment;
        var tokenLogin = CookieTestHelpers.ExtrairValorCookieRefresh(login);
        Assert.False(string.IsNullOrWhiteSpace(tokenLogin));

        var refresh = await client.SendAsync(
            CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, tokenLogin, nomeCookie));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal("/", CookieTestHelpers.ExtrairAtributoCookieRefresh(refresh, "Path"));
        var tokenRefresh = CookieTestHelpers.ExtrairValorCookieRefresh(refresh);
        Assert.False(string.IsNullOrWhiteSpace(tokenRefresh));

        var logout = await client.SendAsync(
            CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaLogout, tokenRefresh, nomeCookie));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var setCookieExpiracao = CookieTestHelpers.ExtrairSetCookie(logout);
        Assert.NotNull(setCookieExpiracao);
        Assert.StartsWith($"{nomeCookie}=", setCookieExpiracao, StringComparison.Ordinal);
        Assert.Equal("/", CookieTestHelpers.ExtrairAtributoCookieRefresh(logout, "Path"));

        // Mesmo client, sem limpar cookies: a renovação seguinte confirma que o logout revogou
        // de fato (nome/Path bateram na hora de ler o cookie apresentado).
        var refreshSeguinte = await client.SendAsync(
            CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, tokenRefresh, nomeCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshSeguinte.StatusCode);
    }

    [Fact]
    public async Task Login_CorpoDaRespostaNuncaContemOValorDoCookie()
    {
        // RF02 — defesa em profundidade adicional à ausência de "RefreshToken" no tipo
        // LoginResponseDto (já garantida em tempo de compilação).
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resposta = await FazerLoginAsync(client);
        var refreshTokenBruto = CookieTestHelpers.ExtrairValorCookieRefresh(resposta);
        Assert.False(string.IsNullOrWhiteSpace(refreshTokenBruto));

        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(refreshTokenBruto!, corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_TokenValido_GravaSucessorComMesmosAtributosEoCorpoNuncaContemOValor()
    {
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var login = await FazerLoginAsync(client);
        var tokenLogin = CookieTestHelpers.ExtrairValorCookieRefresh(login);

        var refresh = await RefreshComCookieAsync(client, tokenLogin);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        Assert.Equal(string.Empty, CookieTestHelpers.ExtrairAtributoCookieRefresh(refresh, "HttpOnly"));
        Assert.Equal("strict", CookieTestHelpers.ExtrairAtributoCookieRefresh(refresh, "SameSite"));
        Assert.Equal("/api/auth", CookieTestHelpers.ExtrairAtributoCookieRefresh(refresh, "Path"));

        var sucessor = CookieTestHelpers.ExtrairValorCookieRefresh(refresh);
        Assert.False(string.IsNullOrWhiteSpace(sucessor));

        var corpo = await refresh.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sucessor!, corpo, StringComparison.Ordinal);
    }

    // ══════════════ D6 — 401 de /refresh nunca expira/toca o cookie ══════════════

    [Theory]
    [InlineData(null)]                 // sem cookie nenhum
    [InlineData("token-inexistente")]  // hash não encontrado no banco
    public async Task Refresh_401_NuncaEmiteSetCookie(string? token)
    {
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resposta = await RefreshComCookieAsync(client, token);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Null(CookieTestHelpers.ExtrairSetCookie(resposta));
    }

    [Fact]
    public async Task Refresh_ReusoRealDetectado_401NaoEmiteSetCookie()
    {
        // Mesmo no ramo mais agressivo (revogação em massa por reuso — issue #62), o 401 de
        // /refresh não pode tocar no cookie (D6): apagar o cookie de uma aba concorrente que
        // tenha vencido a corrida apagaria a sessão legítima dela.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var login = await FazerLoginAsync(client);
        var tokenA = CookieTestHelpers.ExtrairValorCookieRefresh(login);

        var rotacao = await RefreshComCookieAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);

        factory.Relogio.Avancar(TimeSpan.FromSeconds(31)); // fora da janela de graça (30s padrão)

        var reuso = await RefreshComCookieAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);
        Assert.Null(CookieTestHelpers.ExtrairSetCookie(reuso));
    }

    // ══════════════ RF04 — logout sempre expira o cookie, com os mesmos atributos ══════════════

    [Fact]
    public async Task Logout_ExpiraCookieComMesmosAtributosDaGravacaoEDerrubaORefreshSeguinte()
    {
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var login = await FazerLoginAsync(client);
        var token = CookieTestHelpers.ExtrairValorCookieRefresh(login);

        var logout = await LogoutComCookieAsync(client, token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var setCookieExpiracao = CookieTestHelpers.ExtrairSetCookie(logout);
        Assert.NotNull(setCookieExpiracao);

        // Mesmos atributos de identidade do cookie gravado no login (RF04: um cookie com
        // atributos divergentes não é removido pelo navegador, só mascarado).
        Assert.Equal(string.Empty, CookieTestHelpers.ExtrairAtributoCookieRefresh(logout, "HttpOnly"));
        Assert.Equal("strict", CookieTestHelpers.ExtrairAtributoCookieRefresh(logout, "SameSite"));
        Assert.Equal("/api/auth", CookieTestHelpers.ExtrairAtributoCookieRefresh(logout, "Path"));

        // Expiração de fato: Max-Age=0 (ASP.NET Core zera Max-Age e usa Expires=época Unix em
        // Response.Cookies.Delete — ver RefreshTokenCookie.Expirar) ou um valor vazio do cookie.
        var valorExpirado = CookieTestHelpers.ExtrairValorCookieRefresh(logout);
        Assert.True(string.IsNullOrEmpty(valorExpirado));

        // Mesmo client HTTP, sem limpar cookies manualmente: a renovação seguinte não encontra
        // mais um refresh token utilizável (o valor apresentado era o antigo, já revogado).
        var refreshSeguinte = await RefreshComCookieAsync(client, token);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshSeguinte.StatusCode);
    }

    [Fact]
    public async Task Logout_SemCookieNenhum_Retorna204EAindaAssimEmiteSetCookieDeExpiracao()
    {
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var logout = await LogoutComCookieAsync(client, null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.NotNull(CookieTestHelpers.ExtrairSetCookie(logout));
    }

    [Fact]
    public async Task Logout_DuasVezesSeguidas_ContinuaIdempotenteERetorna204()
    {
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var login = await FazerLoginAsync(client);
        var token = CookieTestHelpers.ExtrairValorCookieRefresh(login);

        var primeiro = await LogoutComCookieAsync(client, token);
        Assert.Equal(HttpStatusCode.NoContent, primeiro.StatusCode);

        var segundo = await LogoutComCookieAsync(client, token);
        Assert.Equal(HttpStatusCode.NoContent, segundo.StatusCode);
    }

    // ══════════════ RF12 — as quatro rotas continuam anônimas, sem exigir Bearer ══════════════

    [Theory]
    [InlineData(RotaLogin)]
    [InlineData(RotaRefresh)]
    [InlineData(RotaLogout)]
    [InlineData(RotaTrocarSenhaObrigatoria)]
    public async Task QuatroRotasDeAuth_SemCookieNemHeaderDeAutenticacao_NaoExigemBearer(string rota)
    {
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var requisicao = new HttpRequestMessage(HttpMethod.Post, rota)
        {
            Content = JsonContent.Create(new { })
        };

        var resposta = await client.SendAsync(requisicao);

        // [AllowAnonymous] nas quatro rotas: a ausência de Authorization/cookie não deve
        // produzir o desafio do esquema JWT Bearer (header WWW-Authenticate) — só respostas de
        // negócio (400/401/403/204 específicos de cada rota, cobertos pelas suítes dedicadas).
        Assert.False(resposta.Headers.Contains("WWW-Authenticate"));
    }
}
