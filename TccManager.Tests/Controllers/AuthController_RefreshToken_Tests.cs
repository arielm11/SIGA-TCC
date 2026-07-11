using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Testes de integração da S4 (Refresh Token / Controle de Sessão): login com emissão de
/// refresh token, sessão única (login revoga sessões anteriores), rotação em /api/auth/refresh
/// e revogação em /api/auth/logout.
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

    private static async Task<LoginResponseDto> FazerLogin(HttpClient client, string email = "aluno@teste.com")
    {
        var resp = await client.PostAsJsonAsync(RotaLogin, new LoginDto { Email = email, Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    // ─────────────────────────── Login ───────────────────────────

    [Fact]
    public async Task Login_CredenciaisValidas_RetornaJwtERefreshToken()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory, "login@teste.com");
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "login@teste.com", Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.Token));
        // JWT tem 3 segmentos separados por ponto (header.payload.signature).
        Assert.Equal(3, dto.Token.Split('.').Length);
        Assert.False(string.IsNullOrWhiteSpace(dto.RefreshToken));
        Assert.Equal("Aluno Teste", dto.Nome);
        Assert.Equal("login@teste.com", dto.Email);
    }

    [Fact]
    public async Task Login_PersisteRefreshTokenComoHashNaoOValorBruto()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = factory.CreateClient();

        var dto = await FazerLogin(client);

        using var context = factory.CriarContextoDireto();
        var persistido = context.RefreshTokens.SingleOrDefault();
        Assert.NotNull(persistido);
        // O valor bruto nunca é gravado; grava-se apenas o hash SHA-256.
        Assert.NotEqual(dto.RefreshToken, persistido!.TokenHash);
        Assert.Equal(CalcularHash(dto.RefreshToken), persistido.TokenHash);
        Assert.Null(persistido.RevokedAtUtc);
    }

    // ──────────────────── Sessão única (RF04) ────────────────────

    [Fact]
    public async Task LoginDuasVezes_MesmoUsuario_RevogaRefreshTokenDaPrimeiraSessao()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = factory.CreateClient();

        var primeira = await FazerLogin(client);   // sessão 1
        var segunda = await FazerLogin(client);    // sessão 2 — deve revogar a 1

        Assert.NotEqual(primeira.RefreshToken, segunda.RefreshToken);

        // O refresh token da primeira sessão não pode mais ser usado.
        var refreshAntigo = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = primeira.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAntigo.StatusCode);

        // O refresh token da sessão atual continua válido.
        var refreshAtual = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = segunda.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshAtual.StatusCode);
    }

    // ──────────────────── Refresh / rotação (RF03) ────────────────────

    [Fact]
    public async Task Refresh_TokenValido_RetornaNovoParERotacionaOAntigo()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = factory.CreateClient();

        var login = await FazerLogin(client);

        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = login.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var par = await resp.Content.ReadFromJsonAsync<TokenPairDto>();
        Assert.NotNull(par);
        Assert.False(string.IsNullOrWhiteSpace(par!.Token));
        Assert.Equal(3, par.Token.Split('.').Length);
        Assert.False(string.IsNullOrWhiteSpace(par.RefreshToken));
        // Rotação: o novo refresh token é diferente do apresentado.
        Assert.NotEqual(login.RefreshToken, par.RefreshToken);
        Assert.True(par.ExpiresAtUtc > DateTime.UtcNow);

        // O refresh token antigo deixa de funcionar após a rotação.
        var reuso = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);
    }

    [Fact]
    public async Task Refresh_MarcaTokenAntigoComoRevogadoEApontaParaONovo()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = factory.CreateClient();

        var login = await FazerLogin(client);
        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = login.RefreshToken });
        var par = await resp.Content.ReadFromJsonAsync<TokenPairDto>();

        using var context = factory.CriarContextoDireto();
        var hashAntigo = CalcularHash(login.RefreshToken);
        var hashNovo = CalcularHash(par!.RefreshToken);

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
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenVazio_Retorna401()
    {
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = "" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenExpirado_Retorna401()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuario(factory);
        var client = factory.CreateClient();

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

        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = refreshBruto });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenJaRevogado_Retorna401()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuario(factory);
        var client = factory.CreateClient();

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

        var resp = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = refreshBruto });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ──────────────────── Logout (RF05) ────────────────────

    [Fact]
    public async Task Logout_RevogaRefreshToken_UsoPosteriorFalha()
    {
        var factory = new TccApiFactory();
        await SemearUsuario(factory);
        var client = factory.CreateClient();

        var login = await FazerLogin(client);

        var logout = await client.PostAsJsonAsync(RotaLogout,
            new LogoutRequestDto { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Após logout, o refresh token não pode mais ser usado.
        var refresh = await client.PostAsJsonAsync(RotaRefresh,
            new RefreshRequestDto { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(login.RefreshToken));
        Assert.NotNull(persistido.RevokedAtUtc);
    }

    [Fact]
    public async Task Logout_TokenInexistenteOuVazio_Idempotente_Retorna204()
    {
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var inexistente = await client.PostAsJsonAsync(RotaLogout,
            new LogoutRequestDto { RefreshToken = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.NoContent, inexistente.StatusCode);

        var vazio = await client.PostAsJsonAsync(RotaLogout,
            new LogoutRequestDto { RefreshToken = "" });
        Assert.Equal(HttpStatusCode.NoContent, vazio.StatusCode);
    }
}
