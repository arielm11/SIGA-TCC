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
/// Lote de segurança de <c>AuthTokenService.RefreshAsync</c>:
///
/// • Issue #59 — usuário desativado não renova sessão. Antes, um usuário podia ser desativado
///   pelo Admin e continuar renovando o access token indefinidamente enquanto tivesse um
///   refresh token válido em mãos (a checagem de <c>Ativo</c> só existia no login).
///
/// • Issue #62 — reuse-detection. Um refresh token JÁ ROTACIONADO (revogado E com
///   <c>ReplacedByTokenHash</c> preenchido) sendo reapresentado é evidência de vazamento:
///   o cliente legítimo já avançou na cadeia de rotação. A resposta é revogar TODAS as
///   sessões ativas do usuário, não apenas recusar aquela chamada.
///
/// Isolamento: uma <see cref="TccApiFactory"/> por teste (banco InMemory e rate limiter
/// próprios). O orçamento da política "refresh" é de 15 chamadas/janela por IP, e nenhum
/// teste aqui passa de 4 — não há risco de 429 mascarar um 401.
/// </summary>
public class AuthController_RefreshSeguranca_Tests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";
    private const string SenhaValida = "SenhaValida123";
    private const string EmailPadrao = "sessao@teste.com";

    private static async Task<Usuario> SemearUsuarioAsync(TccApiFactory factory, bool ativo = true)
    {
        using var context = factory.CriarContextoDireto();
        var usuario = new Usuario
        {
            Nome = "Usuario Sessao",
            Email = EmailPadrao,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
            Tipo = TipoUsuario.Aluno,
            Ativo = ativo
        };
        context.Usuarios.Add(usuario);
        await context.SaveChangesAsync();
        return usuario;
    }

    private static async Task DesativarUsuarioAsync(TccApiFactory factory, int usuarioId)
    {
        using var context = factory.CriarContextoDireto();
        var usuario = await context.Usuarios.FindAsync(usuarioId);
        usuario!.Ativo = false;
        await context.SaveChangesAsync();
    }

    /// <summary>Espelha <c>AuthTokenService.CalcularHash</c> (SHA-256 hex minúsculo).</summary>
    private static string CalcularHash(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor))).ToLowerInvariant();

    private static async Task<LoginResponseDto> FazerLoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = EmailPadrao, Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);
        return dto!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync(RotaRefresh, new RefreshRequestDto { RefreshToken = refreshToken });

    // ══════════════════ Issue #59 — usuário desativado ══════════════════

    [Fact]
    public async Task Refresh_UsuarioDesativadoAposLogin_Retorna401MesmoComTokenValido()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        var login = await FazerLoginAsync(client);

        // O refresh token continua íntegro (não revogado, não expirado): a única razão
        // para a recusa é o usuário ter deixado de estar ativo.
        using (var context = factory.CriarContextoDireto())
        {
            var persistido = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(login.RefreshToken));
            Assert.Null(persistido.RevokedAtUtc);
            Assert.True(persistido.ExpiresAtUtc > DateTime.UtcNow);
        }

        await DesativarUsuarioAsync(factory, usuario.Id);

        var resp = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_UsuarioAtivo_ContinuaRenovandoNormalmente()
    {
        // Controle do teste acima: a checagem de Ativo não pode ter quebrado o caminho feliz.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        var login = await FazerLoginAsync(client);

        var resp = await RefreshAsync(client, login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var par = await resp.Content.ReadFromJsonAsync<TokenPairDto>();
        Assert.NotNull(par);
        Assert.NotEqual(login.RefreshToken, par!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_UsuarioDesativado_NaoEmiteNovoRefreshTokenNoBanco()
    {
        // Garante que a recusa acontece ANTES de CriarNovoPar — nada de sessão nova
        // persistida para um usuário desativado.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        var login = await FazerLoginAsync(client);
        await DesativarUsuarioAsync(factory, usuario.Id);

        var resp = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        using var context = factory.CriarContextoDireto();
        var tokensDoUsuario = context.RefreshTokens.Where(rt => rt.UsuarioId == usuario.Id).ToList();
        var unico = Assert.Single(tokensDoUsuario);
        Assert.Equal(CalcularHash(login.RefreshToken), unico.TokenHash);
        Assert.Null(unico.ReplacedByTokenHash);
    }

    [Fact]
    public async Task Login_UsuarioJaCriadoInativo_Retorna401()
    {
        // Regressão do caminho que já existia: a checagem de Ativo no login permanece.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory, ativo: false);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = EmailPadrao, Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ══════════════════ Issue #62 — reuse-detection ══════════════════

    [Fact]
    public async Task Refresh_TokenJaRotacionadoReapresentado_Retorna401EDerrubaASessaoInteira()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        // A: token emitido no login. B: token que substituiu A na rotação.
        var login = await FazerLoginAsync(client);

        var rotacao = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);
        var tokenB = (await rotacao.Content.ReadFromJsonAsync<TokenPairDto>())!.RefreshToken;

        // Reapresentação de A (revogado + ReplacedByTokenHash preenchido) = sinal de vazamento.
        var reuso = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        // Evidência de que a resposta foi "derrubar a sessão" e não apenas "recusar A":
        // o token B, que estava perfeitamente válido, também deixa de funcionar.
        var aposReuso = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistidoB = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(tokenB));
        Assert.NotNull(persistidoB.RevokedAtUtc);

        Assert.DoesNotContain(
            context.RefreshTokens.Where(rt => rt.UsuarioId == usuario.Id).ToList(),
            rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Refresh_TokenApenasExpirado_Retorna401SemDerrubarAsDemaisSessoes()
    {
        // Um token expirado (nunca rotacionado: RevokedAtUtc null, ReplacedByTokenHash null)
        // não é sinal de vazamento — a resposta agressiva não deve disparar.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        var login = await FazerLoginAsync(client);

        var expiradoBruto = Guid.NewGuid().ToString();
        using (var context = factory.CriarContextoDireto())
        {
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(expiradoBruto),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-8),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
                RevokedAtUtc = null
            });
            await context.SaveChangesAsync();
        }

        var recusado = await RefreshAsync(client, expiradoBruto);
        Assert.Equal(HttpStatusCode.Unauthorized, recusado.StatusCode);

        // A sessão legítima segue viva.
        var legitimo = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, legitimo.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenRevogadoSemSubstituto_Retorna401SemDerrubarAsDemaisSessoes()
    {
        // Revogação sem ReplacedByTokenHash é o rastro de um logout manual, não de rotação.
        // Reapresentá-lo deve apenas falhar, sem punir a sessão ativa do usuário.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = factory.CreateClient();

        var login = await FazerLoginAsync(client);

        var revogadoBruto = Guid.NewGuid().ToString();
        using (var context = factory.CriarContextoDireto())
        {
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(revogadoBruto),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                RevokedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                ReplacedByTokenHash = null
            });
            await context.SaveChangesAsync();
        }

        var recusado = await RefreshAsync(client, revogadoBruto);
        Assert.Equal(HttpStatusCode.Unauthorized, recusado.StatusCode);

        var legitimo = await RefreshAsync(client, login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, legitimo.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReusoNaoAfetaSessoesDeOutroUsuario()
    {
        // A revogação em massa é escopada por UsuarioId — o alcance do "modo pânico"
        // precisa parar na fronteira do usuário comprometido.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);

        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario
            {
                Nome = "Outro Usuario",
                Email = "outro@teste.com",
                SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
                Tipo = TipoUsuario.Aluno,
                Ativo = true
            });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClient();

        var loginVitima = await FazerLoginAsync(client);

        var respOutro = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "outro@teste.com", Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, respOutro.StatusCode);
        var loginOutro = (await respOutro.Content.ReadFromJsonAsync<LoginResponseDto>())!;

        var rotacao = await RefreshAsync(client, loginVitima.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);

        var reuso = await RefreshAsync(client, loginVitima.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        var outroSegueValido = await RefreshAsync(client, loginOutro.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, outroSegueValido.StatusCode);
    }
}
