using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Testes de integração da S4 (Refresh Token / Controle de Sessão): login com emissão de
/// refresh token, sessão única (login revoga sessões anteriores), rotação em /api/auth/refresh
/// e revogação em /api/auth/logout.
///
/// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md): o refresh token
/// deixou de viajar no corpo JSON — é entregue via Set-Cookie HttpOnly ("siga_refresh") em
/// /login e /refresh, e lido do cookie da requisição em /refresh e /logout. Os clientes desta
/// suíte usam <see cref="CookieTestHelpers.ClienteSemCookies"/> (jar automático desligado) e
/// controlam o cookie manualmente — necessário porque vários testes aqui reapresentam
/// deliberadamente um valor ESPECÍFICO (ex.: o refresh token da primeira sessão depois de um
/// segundo login), que o jar automático do navegador/HttpClient substituiria silenciosamente.
///
/// Isolamento: cada teste instancia sua própria <see cref="TccApiFactory"/> (host + banco
/// InMemory + PartitionedRateLimiter próprios). Isso garante DB limpo e, importante para esta
/// suíte, que o rate limiter "login" (compartilhado por /login e /refresh — 5 req/janela por IP)
/// não some chamadas entre testes distintos. Cada teste mantém no máximo 4 chamadas às rotas
/// limitadas para nunca esbarrar no limite de 5.
/// </summary>
public class AuthController_RefreshToken_Tests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";
    private const string RotaLogout = "/api/auth/logout";
    private const string SenhaValida = "SenhaValida123";

    private static async Task<Usuario> SemearUsuario(TccApiFactory factory, string email = "aluno@teste.com")
    {
        using var context = factory.CriarContextoDireto();
        var usuario = new Usuario
        {
            Nome = "Aluno Teste",
            Email = email,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };
        context.Usuarios.Add(usuario);
        await context.SaveChangesAsync();
        return usuario;
    }

    /// <summary>
    /// Replica exatamente o algoritmo de <c>AuthTokenService.CalcularHash</c> (SHA-256 em hex
    /// minúsculo) para permitir semear refresh tokens diretamente no banco em cenários que não
    /// podem ser produzidos por um fluxo HTTP normal (expirado, revogado sem passar por logout).
    /// </summary>
    private static string CalcularHash(string valor)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(valor));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static async Task<(LoginResponseDto Dto, string RefreshTokenBruto)> FazerLogin(
        HttpClient client, string email = "aluno@teste.com")
    {
        var resp = await client.PostAsJsonAsync(RotaLogin, new LoginDto { Email = email, Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);

        var refreshTokenBruto = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(refreshTokenBruto));

        return (dto!, refreshTokenBruto!);
    }

    private static Task<HttpResponseMessage> RefreshComCookie(HttpClient client, string? refreshTokenBruto) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, refreshTokenBruto));

    private static Task<HttpResponseMessage> LogoutComCookie(HttpClient client, string? refreshTokenBruto) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaLogout, refreshTokenBruto));

    // ─────────────────────────── Login ───────────────────────────

    [Fact]
    public async Task Login_CredenciaisValidas_RetornaJwtEGravaCookieDeRefresh()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory, "login@teste.com");
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "login@teste.com", Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.Token));
        // JWT tem 3 segmentos separados por ponto (header.payload.signature).
        Assert.Equal(3, dto.Token.Split('.').Length);
        Assert.Equal("Aluno Teste", dto.Nome);
        Assert.Equal("login@teste.com", dto.Email);

        // RF02: o refresh token não vai mais no corpo (LoginResponseDto não tem mais a
        // propriedade — garantido em tempo de compilação) e é entregue só via Set-Cookie.
        var refreshTokenBruto = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(refreshTokenBruto));

        var corpo = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(refreshTokenBruto!, corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_PersisteRefreshTokenComoHashNaoOValorBruto()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, refreshTokenBruto) = await FazerLogin(client);

        using var context = factory.CriarContextoDireto();
        var persistido = context.RefreshTokens.SingleOrDefault();
        Assert.NotNull(persistido);
        // O valor bruto nunca é gravado; grava-se apenas o hash SHA-256.
        Assert.NotEqual(refreshTokenBruto, persistido!.TokenHash);
        Assert.Equal(CalcularHash(refreshTokenBruto), persistido.TokenHash);
        Assert.Null(persistido.RevokedAtUtc);
    }

    // ──────────────────── Sessão única (RF04) ────────────────────

    [Fact]
    public async Task LoginDuasVezes_MesmoUsuario_RevogaRefreshTokenDaPrimeiraSessao()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, primeiroToken) = await FazerLogin(client);   // sessão 1
        var (_, segundoToken) = await FazerLogin(client);    // sessão 2 — deve revogar a 1

        Assert.NotEqual(primeiroToken, segundoToken);

        // O refresh token da primeira sessão não pode mais ser usado.
        var refreshAntigo = await RefreshComCookie(client, primeiroToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAntigo.StatusCode);

        // O refresh token da sessão atual continua válido.
        var refreshAtual = await RefreshComCookie(client, segundoToken);
        Assert.Equal(HttpStatusCode.OK, refreshAtual.StatusCode);
    }

    // ──────────────────── Refresh / rotação (RF03) ────────────────────

    [Fact]
    public async Task Refresh_TokenValido_RetornaNovoParERotacionaOAntigo()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLogin(client);

        var resp = await RefreshComCookie(client, loginToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var corpo = await resp.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.NotNull(corpo);
        Assert.False(string.IsNullOrWhiteSpace(corpo!.Token));
        Assert.Equal(3, corpo.Token.Split('.').Length);

        var novoRefreshToken = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(novoRefreshToken));
        // Rotação: o novo refresh token é diferente do apresentado.
        Assert.NotEqual(loginToken, novoRefreshToken);

        // Issue #85: reapresentar o token antigo IMEDIATAMENTE após a rotação deixou de ser 401.
        // Essa sequência é, por definição, a corrida benigna (duas abas renovando quase juntas),
        // e a resposta passou a ser o replay idempotente do par vigente. O token antigo continua
        // sem produzir uma sessão nova — ele devolve exatamente o mesmo sucessor já emitido.
        // O 401 permanece fora da janela de graça e nos demais casos de reuso real, cobertos por
        // AuthController_RefreshJanelaDeGraca_Tests e AuthController_RefreshSeguranca_Tests.
        var reapresentacao = await RefreshComCookie(client, loginToken);
        Assert.Equal(HttpStatusCode.OK, reapresentacao.StatusCode);

        var replaySucessor = CookieTestHelpers.ExtrairValorCookieRefresh(reapresentacao);
        Assert.Equal(novoRefreshToken, replaySucessor);
    }

    [Fact]
    public async Task Refresh_MarcaTokenAntigoComoRevogadoEApontaParaONovo()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLogin(client);
        var resp = await RefreshComCookie(client, loginToken);
        var novoRefreshToken = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(novoRefreshToken));

        using var context = factory.CriarContextoDireto();
        var hashAntigo = CalcularHash(loginToken);
        var hashNovo = CalcularHash(novoRefreshToken!);

        var antigo = context.RefreshTokens.Single(rt => rt.TokenHash == hashAntigo);
        Assert.NotNull(antigo.RevokedAtUtc);
        Assert.Equal(hashNovo, antigo.ReplacedByTokenHash);

        var novo = context.RefreshTokens.Single(rt => rt.TokenHash == hashNovo);
        Assert.Null(novo.RevokedAtUtc);
    }

    [Fact]
    public async Task Refresh_TokenInexistente_Retorna401()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resp = await RefreshComCookie(client, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_SemCookie_Retorna401()
    {
        // RF03: sem o cookie, o comportamento reproduz exatamente o ramo anterior de corpo
        // vazio/RefreshToken vazio.
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resp = await RefreshComCookie(client, null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenExpirado_Retorna401()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var refreshBruto = Guid.NewGuid().ToString();
        using (var context = factory.CriarContextoDireto())
        {
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(refreshBruto),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-8),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1), // já expirado
                RevokedAtUtc = null
            });
            await context.SaveChangesAsync();
        }

        var resp = await RefreshComCookie(client, refreshBruto);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenJaRevogado_Retorna401()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var refreshBruto = Guid.NewGuid().ToString();
        using (var context = factory.CriarContextoDireto())
        {
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(refreshBruto),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                RevokedAtUtc = DateTime.UtcNow.AddMinutes(-1) // revogado, ainda não expirado
            });
            await context.SaveChangesAsync();
        }

        var resp = await RefreshComCookie(client, refreshBruto);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ──────────────────── Logout (RF05) ────────────────────

    [Fact]
    public async Task Logout_RevogaRefreshToken_UsoPosteriorFalha()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLogin(client);

        var logout = await LogoutComCookie(client, loginToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Após logout, o refresh token não pode mais ser usado.
        var refresh = await RefreshComCookie(client, loginToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(loginToken));
        Assert.NotNull(persistido.RevokedAtUtc);
    }

    [Fact]
    public async Task Logout_TokenInexistenteOuVazio_Idempotente_Retorna204()
    {
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var inexistente = await LogoutComCookie(client, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, inexistente.StatusCode);

        var semCookie = await LogoutComCookie(client, null);
        Assert.Equal(HttpStatusCode.NoContent, semCookie.StatusCode);
    }
}
