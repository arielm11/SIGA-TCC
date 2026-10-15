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
/// • Issue #85 — a premissa acima era ampla demais: a reapresentação de um token rotacionado
///   também acontece em cenários benignos (corrida entre abas, resposta perdida, F5 no meio do
///   <c>/refresh</c>). Os dois testes desta seção que reapresentavam o token IMEDIATAMENTE após
///   a rotação descreviam, sem saber, o falso positivo — hoje isso é corrida benigna (200 com
///   replay idempotente). Cada um foi dividido em dois: o par benigno (dentro da janela) e o par
///   de reuso real (fora da janela, com o relógio do host avançado). O detalhamento da janela de
///   graça vive em <see cref="AuthController_RefreshJanelaDeGraca_Tests"/>; aqui ficam apenas os
///   pares que preservam a intenção original destes testes.
///
/// • Issue #171 — o refresh token passou a ser transportado via cookie HttpOnly ("siga_refresh"),
///   não mais no corpo. Os testes usam <see cref="CookieTestHelpers.ClienteSemCookies"/> e
///   controlam o cookie manualmente (ver justificativa no cabeçalho de
///   <c>AuthController_RefreshToken_Tests</c>).
///
/// Isolamento: uma <see cref="TccApiFactory"/> por teste (banco InMemory e rate limiter
/// próprios). O orçamento da política "refresh" é de 15 chamadas/janela por IP, e nenhum
/// teste aqui passa de 5 — não há risco de 429 mascarar um 401.
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

    private static async Task<(LoginResponseDto Dto, string RefreshTokenBruto)> FazerLoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = EmailPadrao, Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);

        var refreshTokenBruto = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(refreshTokenBruto));

        return (dto!, refreshTokenBruto!);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string? refreshToken) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, refreshToken));

    // ══════════════════ Issue #59 — usuário desativado ══════════════════

    [Fact]
    public async Task Refresh_UsuarioDesativadoAposLogin_Retorna401MesmoComTokenValido()
    {
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLoginAsync(client);

        // O refresh token continua íntegro (não revogado, não expirado): a única razão
        // para a recusa é o usuário ter deixado de estar ativo.
        using (var context = factory.CriarContextoDireto())
        {
            var persistido = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(loginToken));
            Assert.Null(persistido.RevokedAtUtc);
            Assert.True(persistido.ExpiresAtUtc > DateTime.UtcNow);
        }

        await DesativarUsuarioAsync(factory, usuario.Id);

        var resp = await RefreshAsync(client, loginToken);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_UsuarioAtivo_ContinuaRenovandoNormalmente()
    {
        // Controle do teste acima: a checagem de Ativo não pode ter quebrado o caminho feliz.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLoginAsync(client);

        var resp = await RefreshAsync(client, loginToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var novoRefreshToken = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(novoRefreshToken));
        Assert.NotEqual(loginToken, novoRefreshToken);
    }

    [Fact]
    public async Task Refresh_UsuarioDesativado_NaoEmiteNovoRefreshTokenNoBanco()
    {
        // Garante que a recusa acontece ANTES de CriarNovoPar — nada de sessão nova
        // persistida para um usuário desativado.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLoginAsync(client);
        await DesativarUsuarioAsync(factory, usuario.Id);

        var resp = await RefreshAsync(client, loginToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        using var context = factory.CriarContextoDireto();
        var tokensDoUsuario = context.RefreshTokens.Where(rt => rt.UsuarioId == usuario.Id).ToList();
        var unico = Assert.Single(tokensDoUsuario);
        Assert.Equal(CalcularHash(loginToken), unico.TokenHash);
        Assert.Null(unico.ReplacedByTokenHash);
    }

    [Fact]
    public async Task Login_UsuarioJaCriadoInativo_Retorna401()
    {
        // Regressão do caminho que já existia: a checagem de Ativo no login permanece.
        var factory = new TccApiFactory();
        await SemearUsuarioAsync(factory, ativo: false);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = EmailPadrao, Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ══════════════════ Issue #62 — reuse-detection ══════════════════

    [Fact]
    public async Task Refresh_TokenJaRotacionadoReapresentadoForaDaJanela_Retorna401EDerrubaASessaoInteira()
    {
        // Metade "reuso real" do teste original (issue #85). A sequência é idêntica à de antes;
        // a única diferença é avançar o relógio do host para além da janela de graça, que é o que
        // distingue vazamento de corrida entre abas. Sem isso, a reapresentação imediata seria
        // classificada como benigna — e era exatamente esse falso positivo que a #85 corrigiu.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        // A: token emitido no login. B: token que substituiu A na rotação.
        var (_, tokenA) = await FazerLoginAsync(client);

        var rotacao = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);
        var tokenB = CookieTestHelpers.ExtrairValorCookieRefresh(rotacao);
        Assert.False(string.IsNullOrWhiteSpace(tokenB));

        factory.Relogio.Avancar(TimeSpan.FromSeconds(31));

        // Reapresentação tardia de A (revogado + ReplacedByTokenHash preenchido) = sinal de vazamento.
        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        // Evidência de que a resposta foi "derrubar a sessão" e não apenas "recusar A":
        // o token B, que estava perfeitamente válido, também deixa de funcionar.
        var aposReuso = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistidoB = context.RefreshTokens.Single(rt => rt.TokenHash == CalcularHash(tokenB!));
        Assert.NotNull(persistidoB.RevokedAtUtc);

        Assert.DoesNotContain(
            context.RefreshTokens.Where(rt => rt.UsuarioId == usuario.Id).ToList(),
            rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Refresh_TokenJaRotacionadoReapresentadoDentroDaJanela_Retorna200EMantemASessaoInteira()
    {
        // Metade "corrida benigna" do teste original: a MESMA sequência que antes exigia 401 hoje
        // exige 200 com replay idempotente. A mudança de expectativa é deliberada e é o coração da
        // issue #85 — a reapresentação imediata é a assinatura de duas abas concorrendo, não de um
        // token vazado (seção 9.1 do documento de arquitetura: "isso não é uma regressão, é o
        // teste descrevendo o bug").
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, tokenA) = await FazerLoginAsync(client);

        var rotacao = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);
        var tokenB = CookieTestHelpers.ExtrairValorCookieRefresh(rotacao);
        Assert.False(string.IsNullOrWhiteSpace(tokenB));

        var replay = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Idempotência: o perdedor da corrida recebe o par que a aba vencedora já tem.
        var replaySucessor = CookieTestHelpers.ExtrairValorCookieRefresh(replay);
        Assert.Equal(tokenB, replaySucessor);
        var corpoReplay = await replay.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.False(string.IsNullOrWhiteSpace(corpoReplay!.Token));

        // Nada foi revogado: a sessão da aba vencedora segue de pé, que é o sintoma que o
        // usuário percebia (as duas abas deslogadas sem ter feito nada).
        var aposReplay = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.OK, aposReplay.StatusCode);

        using var context = factory.CriarContextoDireto();
        var tokensDoUsuario = context.RefreshTokens.Where(rt => rt.UsuarioId == usuario.Id).ToList();
        Assert.Contains(tokensDoUsuario, rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Refresh_TokenApenasExpirado_Retorna401SemDerrubarAsDemaisSessoes()
    {
        // Um token expirado (nunca rotacionado: RevokedAtUtc null, ReplacedByTokenHash null)
        // não é sinal de vazamento — a resposta agressiva não deve disparar.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLoginAsync(client);

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
        var legitimo = await RefreshAsync(client, loginToken);
        Assert.Equal(HttpStatusCode.OK, legitimo.StatusCode);
    }

    [Fact]
    public async Task Refresh_TokenRevogadoSemSubstituto_Retorna401SemDerrubarAsDemaisSessoes()
    {
        // Revogação sem ReplacedByTokenHash é o rastro de um logout manual, não de rotação.
        // Reapresentá-lo deve apenas falhar, sem punir a sessão ativa do usuário.
        var factory = new TccApiFactory();
        var usuario = await SemearUsuarioAsync(factory);
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, loginToken) = await FazerLoginAsync(client);

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

        var legitimo = await RefreshAsync(client, loginToken);
        Assert.Equal(HttpStatusCode.OK, legitimo.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReusoRealForaDaJanela_NaoAfetaSessoesDeOutroUsuario()
    {
        // A revogação em massa é escopada por UsuarioId — o alcance do "modo pânico"
        // precisa parar na fronteira do usuário comprometido. Como o reuso real só é
        // alcançável fora da janela de graça (issue #85), o relógio do host é avançado
        // antes da reapresentação; o resto do teste é o original.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var (tokenVitima, tokenOutro, client) = await SemearDoisUsuariosLogadosAsync(factory);

        var rotacao = await RefreshAsync(client, tokenVitima);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);

        factory.Relogio.Avancar(TimeSpan.FromSeconds(31));

        var reuso = await RefreshAsync(client, tokenVitima);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        var outroSegueValido = await RefreshAsync(client, tokenOutro);
        Assert.Equal(HttpStatusCode.OK, outroSegueValido.StatusCode);
    }

    [Fact]
    public async Task Refresh_CorridaBenigna_NaoAfetaSessoesDeOutroUsuarioNemAPropria()
    {
        // Contraparte benigna: a mesma sequência dentro da janela não revoga nada de ninguém —
        // nem do outro usuário (fronteira do escopo) nem do próprio (que era o falso positivo).
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var (tokenVitima, tokenOutro, client) = await SemearDoisUsuariosLogadosAsync(factory);

        var rotacao = await RefreshAsync(client, tokenVitima);
        Assert.Equal(HttpStatusCode.OK, rotacao.StatusCode);
        var tokenB = CookieTestHelpers.ExtrairValorCookieRefresh(rotacao);

        var replay = await RefreshAsync(client, tokenVitima);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(tokenB, CookieTestHelpers.ExtrairValorCookieRefresh(replay));

        var outroSegueValido = await RefreshAsync(client, tokenOutro);
        Assert.Equal(HttpStatusCode.OK, outroSegueValido.StatusCode);
    }

    /// <summary>
    /// Duas contas ativas, cada uma logada com seu próprio cliente sem cookies (os dois sobre o
    /// mesmo host/factory). Devolve o refresh token bruto de cada sessão e o cliente da vítima,
    /// usado nas chamadas subsequentes de /refresh — a fronteira entre elas é o que os dois
    /// testes acima exercitam.
    /// </summary>
    private static async Task<(string TokenVitima, string TokenOutro, HttpClient Client)>
        SemearDoisUsuariosLogadosAsync(TccApiFactory factory)
    {
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

        var client = CookieTestHelpers.ClienteSemCookies(factory);

        var (_, tokenVitima) = await FazerLoginAsync(client);

        var respOutro = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "outro@teste.com", Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, respOutro.StatusCode);
        var tokenOutro = CookieTestHelpers.ExtrairValorCookieRefresh(respOutro);
        Assert.False(string.IsNullOrWhiteSpace(tokenOutro));

        return (tokenVitima, tokenOutro!, client);
    }
}
