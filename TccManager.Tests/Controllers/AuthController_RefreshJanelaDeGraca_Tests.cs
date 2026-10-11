using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Serilog.Events;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #85 — janela de graça da reuse-detection de refresh token.
///
/// A reapresentação de um refresh token JÁ ROTACIONADO deixou de ser sempre reuso: passou a ser
/// classificada em duas categorias mutuamente exclusivas (D2 do documento de arquitetura
/// <c>docs/arquitetura/2026-09-03-reuse-detection-falso-positivo-multi-aba.md</c>):
///
/// <list type="bullet">
/// <item><b>Corrida benigna</b> — dentro da janela (<c>Jwt:RefreshReuseGraceSeconds</c>, 30 s por
/// padrão) <b>e</b> com o sucessor ainda sendo a ponta ativa da cadeia: <c>200</c> com replay
/// idempotente (o mesmo refresh token sucessor + um access token novo), sem escrever no banco.</item>
/// <item><b>Reuso real</b> — fora da janela <b>ou</b> com o sucessor já revogado/rotacionado:
/// comportamento integral da issue #62 (<c>Warning</c>, revogação de todas as sessões, <c>401</c>).</item>
/// </list>
///
/// Issue #171 — o refresh token passou a ser transportado via cookie HttpOnly ("siga_refresh").
/// Esta suíte é exatamente o cenário que motivou a recomendação da seção 9 do documento de
/// arquitetura de desligar o jar automático de cookies: o "perdedor" da corrida precisa
/// reapresentar deliberadamente um valor (token A) que o jar substituiria pelo sucessor (token B)
/// assim que a resposta da rotação chegasse. Por isso todos os clientes aqui usam
/// <see cref="CookieTestHelpers.ClienteSemCookies"/> e o cookie é sempre controlado manualmente.
///
/// Todos os testes usam <see cref="RefreshJanelaDeGracaApiFactory"/> (relógio controlável +
/// configuração da janela + captura de log). Nada aqui depende de <c>Thread.Sleep</c>: sair da
/// janela é avançar o <see cref="RelogioAjustavelTimeProvider"/>.
///
/// Isolamento: uma factory por teste (banco InMemory, cache em memória e rate limiter próprios).
/// A política "refresh" permite 15 chamadas/janela por IP e nenhum teste aqui passa de 5.
/// </summary>
public class AuthController_RefreshJanelaDeGraca_Tests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";
    private const string RotaLogout = "/api/auth/logout";
    private const string SenhaValida = "SenhaValida123";
    private const string EmailPadrao = "corrida@teste.com";

    /// <summary>Janela padrão de produção (<c>appsettings.json</c>), em segundos.</summary>
    private static readonly TimeSpan JanelaPadrao = TimeSpan.FromSeconds(30);

    // ─────────────────────────── infraestrutura do arquivo ───────────────────────────

    private static async Task<Usuario> SemearUsuarioAsync(
        RefreshJanelaDeGracaApiFactory factory, string email = EmailPadrao)
    {
        using var context = factory.CriarContextoDireto();
        var usuario = new Usuario
        {
            Nome = "Usuario Corrida",
            Email = email,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };
        context.Usuarios.Add(usuario);
        await context.SaveChangesAsync();
        return usuario;
    }

    /// <summary>Espelha <c>AuthTokenService.CalcularHash</c> (SHA-256 hex minúsculo).</summary>
    private static string CalcularHash(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor))).ToLowerInvariant();

    private static async Task<string> FazerLoginAsync(HttpClient client, string email = EmailPadrao)
    {
        var resp = await client.PostAsJsonAsync(RotaLogin, new LoginDto { Email = email, Senha = SenhaValida });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);

        var refreshTokenBruto = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(refreshTokenBruto));
        return refreshTokenBruto!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string? refreshToken) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaRefresh, refreshToken));

    private static Task<HttpResponseMessage> LogoutAsync(HttpClient client, string? refreshToken) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(HttpMethod.Post, RotaLogout, refreshToken));

    private static async Task<(string Token, string NovoRefreshToken)> RotacionarAsync(HttpClient client, string refreshToken)
    {
        var resp = await RefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var corpo = await resp.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.NotNull(corpo);
        var novoRefreshToken = CookieTestHelpers.ExtrairValorCookieRefresh(resp);
        Assert.False(string.IsNullOrWhiteSpace(novoRefreshToken));
        return (corpo!.Token, novoRefreshToken!);
    }

    /// <summary>
    /// Login + uma rotação: devolve o token apresentado (A, já rotacionado) e o sucessor (B, ponta
    /// ativa da cadeia). É o estado a partir do qual toda a classificação desta issue acontece.
    /// </summary>
    private static async Task<(Usuario Usuario, string TokenA, string TokenB)> LoginERotacaoAsync(
        RefreshJanelaDeGracaApiFactory factory, HttpClient client)
    {
        var usuario = await SemearUsuarioAsync(factory);
        var tokenA = await FazerLoginAsync(client);
        var (_, tokenB) = await RotacionarAsync(client, tokenA);
        return (usuario, tokenA, tokenB);
    }

    private static List<RefreshToken> TokensDoUsuario(RefreshJanelaDeGracaApiFactory factory, int usuarioId)
    {
        using var context = factory.CriarContextoDireto();
        return context.RefreshTokens.Where(rt => rt.UsuarioId == usuarioId).ToList();
    }

    // ══════════════ T1 — corrida benigna: replay idempotente dentro da janela ══════════════

    [Fact]
    public async Task CorridaBenigna_ReapresentacaoDentroDaJanela_Retorna200ComOMesmoRefreshTokenSucessor()
    {
        // Núcleo da correção: a aba perdedora reapresenta A e recebe o par vigente, em vez de
        // 401 + sessão derrubada.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (_, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.Relogio.Avancar(TimeSpan.FromSeconds(1));

        var resposta = await RefreshAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var replaySucessor = CookieTestHelpers.ExtrairValorCookieRefresh(resposta);
        Assert.Equal(tokenB, replaySucessor);
        var corpo = await resposta.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.False(string.IsNullOrWhiteSpace(corpo!.Token));
    }

    [Fact]
    public async Task CorridaBenigna_ReplayEmiteAccessTokenValidoParaOMesmoUsuario()
    {
        // O contrato (seção 7) diz que o replay devolve o MESMO refresh token e um access token
        // recém-emitido. Não dá para asserir "Token diferente do anterior" de forma determinística
        // (TokenService ainda usa DateTime.UtcNow e o JWT tem precisão de segundo — dois tokens
        // gerados no mesmo segundo para o mesmo usuário são byte a byte iguais), então o que se
        // trava aqui é o que importa: veio um JWT bem formado e com as claims do usuário certo.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, _) = await LoginERotacaoAsync(factory, client);

        var resposta = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var replay = (await resposta.Content.ReadFromJsonAsync<RefreshResponseDto>())!;
        Assert.Equal(3, replay.Token.Split('.').Length);

        // ReadJwtToken não aplica o InboundClaimTypeMap (isso só acontece em ValidateToken), então
        // as claims chegam com o nome curto do JWT ("nameid"/"role"). Aceitar as duas formas evita
        // que o teste quebre por causa da configuração de mapeamento, que não é o objeto aqui.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(replay.Token);

        var claimUsuario = Assert.Single(
            jwt.Claims, c => c.Type is "nameid" or ClaimTypes.NameIdentifier);
        Assert.Equal(usuario.Id.ToString(), claimUsuario.Value);

        var claimPapel = Assert.Single(jwt.Claims, c => c.Type is "role" or ClaimTypes.Role);
        Assert.Equal(TipoUsuario.Aluno.ToString(), claimPapel.Value);
    }

    [Fact]
    public async Task CorridaBenigna_NaoRevogaNadaENaoEscreveNovoTokenNoBanco()
    {
        // "Nenhuma escrita no banco no caminho benigno" é propriedade desejada (seção 5.2), e a
        // consequência visível para o usuário é a sessão continuar de pé.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var resposta = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var tokens = TokensDoUsuario(factory, usuario.Id);
        Assert.Equal(2, tokens.Count);   // A e B; o replay não criou uma terceira linha

        var persistidoB = tokens.Single(rt => rt.TokenHash == CalcularHash(tokenB));
        Assert.Null(persistidoB.RevokedAtUtc);

        var persistidoA = tokens.Single(rt => rt.TokenHash == CalcularHash(tokenA));
        Assert.Equal(RefreshJanelaDeGracaApiFactory.InstanteInicial.UtcDateTime, persistidoA.RevokedAtUtc);
        Assert.Equal(persistidoB.TokenHash, persistidoA.ReplacedByTokenHash);
    }

    [Fact]
    public async Task CorridaBenigna_SucessorContinuaFuncionandoDepoisDoReplay()
    {
        // Prova de que a aba vencedora não foi punida pela perdedora — era exatamente isso que o
        // bug fazia (RevokeAllForUserAsync matava o B recém-emitido).
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (_, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var replay = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var (_, proximoSucessor) = await RotacionarAsync(client, tokenB);
        Assert.NotEqual(tokenB, proximoSucessor);
    }

    // ══════════════ T2 — replay repetido (três abas, dois perdedores) ══════════════

    [Fact]
    public async Task CorridaBenigna_DuasReapresentacoesDentroDaJanela_AmbasRecebemOMesmoPar()
    {
        // Com três abas há dois perdedores, e ambos precisam conseguir ler a mesma entrada de
        // cache: a decisão explícita (seção 5.2) é NÃO invalidar o replay na primeira leitura.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var primeiroReplay = await RefreshAsync(client, tokenA);
        factory.Relogio.Avancar(TimeSpan.FromSeconds(2));
        var segundoReplay = await RefreshAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.OK, primeiroReplay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segundoReplay.StatusCode);

        Assert.Equal(tokenB, CookieTestHelpers.ExtrairValorCookieRefresh(primeiroReplay));
        Assert.Equal(tokenB, CookieTestHelpers.ExtrairValorCookieRefresh(segundoReplay));

        Assert.Equal(2, TokensDoUsuario(factory, usuario.Id).Count);
    }

    // ══════════════ T3 — a fronteira da janela ══════════════

    [Fact]
    public async Task CorridaBenigna_ExatamenteNoLimiteDaJanela_AindaEhBenigna()
    {
        // A comparação da implementação é "<= janela": o limite exato pertence ao lado benigno.
        // O relógio congelado é o que torna essa fronteira testável sem flakiness.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (_, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.Relogio.Avancar(JanelaPadrao);

        var resposta = await RefreshAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(tokenB, CookieTestHelpers.ExtrairValorCookieRefresh(resposta));
    }

    [Fact]
    public async Task ReusoReal_UmSegundoAlemDaJanela_Retorna401ERevogaTodasAsSessoes()
    {
        // Contraprova do teste acima e garantia de que a detecção da issue #62 continua inteira
        // fora da janela. Note que a entrada de replay ainda existe no cache (o IMemoryCache usa
        // o relógio real, que não andou): a recusa vem da classificação, não do TTL.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.Relogio.Avancar(JanelaPadrao + TimeSpan.FromSeconds(1));

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        // O sucessor, que estava perfeitamente válido, morre junto — é a resposta "derrubar a
        // sessão inteira", não apenas "recusar A".
        var aposReuso = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);

        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    // ══════════ T4/T5 — cadeia de rotações: a corrida benigna agora reconhece 2+ saltos ══════════
    //
    // Achado L1 da revisão de segurança de #171 (ver seção 6.5 do documento de arquitetura).
    // Cenário de 3 abas: A e C mandam RT0 quase juntas; A vence (RT0→RT1). Antes de A gravar o
    // authToken, B já manda /refresh com RT1 (rotação legítima, RT1→RT2). RT0 de C chega dentro
    // da janela de graça, mas o sucessor RT1 já não é mais a ponta ativa — isso NÃO pode mais
    // disparar a revogação em massa: é 100% tráfego legítimo do próprio usuário.

    [Fact]
    public async Task CorridaBenigna_CadeiaDeDoisSaltosDentroDaJanela_Retorna200ComAPontaAtivaENaoRevogaNada()
    {
        // A -> B -> C, os dois saltos dentro da janela. Reapresentar A (o "C" das três abas do
        // cenário acima) tem que devolver o par vigente baseado em C, sem revogar nada — nem a
        // sessão que já avançou para C.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var (_, tokenC) = await RotacionarAsync(client, tokenB);

        factory.Relogio.Avancar(TimeSpan.FromSeconds(1));   // continua dentro da janela

        var replay = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(tokenC, CookieTestHelpers.ExtrairValorCookieRefresh(replay));
        var corpo = await replay.Content.ReadFromJsonAsync<RefreshResponseDto>();
        Assert.False(string.IsNullOrWhiteSpace(corpo!.Token));

        // A sessão continua de pé: C (a ponta ativa de verdade) segue funcionando depois do
        // replay — era exatamente isso que a revogação em massa indevida quebrava.
        var aposReplay = await RefreshAsync(client, tokenC);
        Assert.Equal(HttpStatusCode.OK, aposReplay.StatusCode);

        Assert.Contains(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task ReusoReal_CadeiaComUmSaltoForaDaJanela_Retorna401ERevogaTudo()
    {
        // Mesma cadeia A -> B -> C, mas o salto A->B em si levou mais que a janela de graça (o
        // relógio avança ANTES da segunda rotação). A generalização é por salto, não pelo tempo
        // total até agora: um salto fora da janela já é o suficiente para classificar como reuso
        // real, mesmo que o salto seguinte (B->C) tenha sido rápido.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.Relogio.Avancar(JanelaPadrao + TimeSpan.FromSeconds(1)); // salto A->B "demorou" demais

        var (_, tokenC) = await RotacionarAsync(client, tokenB); // B->C, salto rápido

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        var aposReuso = await RefreshAsync(client, tokenC);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);

        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task ReusoReal_CadeiaMaiorQueOLimiteDeSaltos_Retorna401ERevogaTudoMesmoComTodosOsSaltosDentroDaJanela()
    {
        // Seis rotações em sequência, cada salto individualmente dentro da janela (1s cada, bem
        // abaixo dos 30s). Sem um limite de saltos, encadear vários saltos rápidos poderia
        // transformar a janela de graça numa janela sem fim (N saltos × janela de tempo cada) —
        // o que a seção 6.5 do documento de arquitetura explicitamente proíbe. Reapresentar o
        // token mais antigo da cadeia, além do limite, ainda precisa cair em reuso real.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenAnterior) = await LoginERotacaoAsync(factory, client);

        // Já temos 1 salto (A->tokenAnterior). Mais 5 saltos = 6 no total, 1 acima do limite
        // (MaxSaltosCadeiaReplay = 5 em AuthTokenService).
        for (var i = 0; i < 5; i++)
        {
            factory.Relogio.Avancar(TimeSpan.FromSeconds(1));
            (_, tokenAnterior) = await RotacionarAsync(client, tokenAnterior);
        }

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task ReusoReal_SucessorRevogadoPorLogout_DentroDaJanela_Retorna401()
    {
        // Mesma condição (c) por outro caminho: o sucessor existe, mas foi revogado manualmente.
        // A cadeia não tem mais ponta ativa, então não há par vigente para replicar.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var logout = await LogoutAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        factory.Relogio.Avancar(TimeSpan.FromSeconds(1));

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task ReusoReal_SucessorExpiradoMasNaoRevogado_DentroDaJanela_Retorna401()
    {
        // Terceira forma de "sucessor não é ponta ativa": ele não foi revogado, mas expirou. Esse
        // estado não é produzível por HTTP (a rotação sempre emite o sucessor com 7 dias de vida)
        // sem também sair da janela, então a cadeia é semeada direto no contexto — mesma técnica
        // já usada nos testes de token expirado/revogado da issue #62.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var usuario = await SemearUsuarioAsync(factory);

        var agora = RefreshJanelaDeGracaApiFactory.InstanteInicial.UtcDateTime;
        var tokenA = Guid.NewGuid().ToString();
        var tokenB = Guid.NewGuid().ToString();

        using (var context = factory.CriarContextoDireto())
        {
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(tokenA),
                CreatedAtUtc = agora.AddMinutes(-1),
                ExpiresAtUtc = agora.AddDays(7),
                RevokedAtUtc = agora,                       // rotacionado agora: dentro da janela
                ReplacedByTokenHash = CalcularHash(tokenB)
            });
            context.RefreshTokens.Add(new RefreshToken
            {
                UsuarioId = usuario.Id,
                TokenHash = CalcularHash(tokenB),
                CreatedAtUtc = agora.AddMinutes(-1),
                ExpiresAtUtc = agora.AddSeconds(-1),        // sucessor expirado, mas não revogado
                RevokedAtUtc = null
            });
            await context.SaveChangesAsync();
        }

        var reuso = await RefreshAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);
        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    // ══════════════ T6 — guarda de Ativo no replay (regressão da issue #59) ══════════════

    [Fact]
    public async Task CorridaBenigna_UsuarioDesativado_Retorna401ENaoEmiteAccessToken()
    {
        // D7, o caso marcado como OBRIGATÓRIO pelo documento de arquitetura: o replay também
        // emite um JWT, então a issue #59 (usuário desativado não renova sessão) regrediria pela
        // porta dos fundos se a guarda de Ativo existisse só no caminho normal de rotação.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, _) = await LoginERotacaoAsync(factory, client);

        using (var context = factory.CriarContextoDireto())
        {
            var persistido = await context.Usuarios.FindAsync(usuario.Id);
            persistido!.Ativo = false;
            await context.SaveChangesAsync();
        }

        factory.Relogio.Avancar(TimeSpan.FromSeconds(1));   // dentro da janela

        var resposta = await RefreshAsync(client, tokenA);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);

        // "Nenhum access token emitido" é o ponto do teste: o 401 não pode vir acompanhado de
        // corpo com par de tokens.
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", corpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorridaBenigna_UsuarioDesativado_RecusaSemRevogacaoEmMassa()
    {
        // A recusa por usuário inativo não é acusação de vazamento: é o fail-safe do ramo benigno.
        // Não pode disparar o "modo pânico" nem o Warning de reuso.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        using (var context = factory.CriarContextoDireto())
        {
            var persistido = await context.Usuarios.FindAsync(usuario.Id);
            persistido!.Ativo = false;
            await context.SaveChangesAsync();
        }

        var resposta = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);

        var persistidoB = TokensDoUsuario(factory, usuario.Id).Single(rt => rt.TokenHash == CalcularHash(tokenB));
        Assert.Null(persistidoB.RevokedAtUtc);

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Reuso de refresh token detectado", StringComparison.Ordinal));
    }

    // ══════════════ T7 — interruptor de reversão: janela = 0 ══════════════

    [Fact]
    public async Task JanelaZero_ReapresentacaoImediata_VoltaAoComportamentoEstritoDaIssue62()
    {
        // D5/5.3: "0" desliga a janela sem redeploy de código. Inclui o caso-limite
        // agora == RevokedAtUtc (relógio congelado), que uma comparação "<= 0" literal
        // classificaria erroneamente como benigno.
        using var factory = new RefreshJanelaDeGracaApiFactory(janelaSegundos: 0);
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        var aposReuso = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.Unauthorized, aposReuso.StatusCode);

        Assert.DoesNotContain(TokensDoUsuario(factory, usuario.Id), rt => rt.RevokedAtUtc == null);
    }

    [Fact]
    public async Task JanelaZero_RotacaoNormalNaoQuebraPorTtlDeCacheInvalido()
    {
        // MemoryCacheEntryOptions.AbsoluteExpirationRelativeToNow lança para valores <= 0. Sem a
        // guarda explícita na gravação do replay, ligar o interruptor de reversão derrubaria o
        // caminho quente do /refresh com 500 — pior do que o bug que se quer reverter.
        using var factory = new RefreshJanelaDeGracaApiFactory(janelaSegundos: 0);
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        await SemearUsuarioAsync(factory);

        var loginToken = await FazerLoginAsync(client);

        var primeira = await RefreshAsync(client, loginToken);
        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        var sucessor1 = CookieTestHelpers.ExtrairValorCookieRefresh(primeira);

        var (_, sucessor2) = await RotacionarAsync(client, sucessor1!);
        Assert.False(string.IsNullOrWhiteSpace(sucessor2));

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.Level >= LogEventLevel.Error);
    }

    // ══════════════ T8 — fail-safe de cache frio ══════════════

    [Fact]
    public async Task CacheMissDentroDaJanela_Retorna401SemRevogacaoEmMassa()
    {
        // D6: um cache frio (restart da API, TTL vencido antes da leitura, multi-instância sem
        // afinidade) não pode ser confundido com evidência de vazamento. Recusa a chamada, mas
        // não derruba a sessão — o sucessor continua utilizável.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.LimparCacheDeReplay();

        var resposta = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);

        var persistidoB = TokensDoUsuario(factory, usuario.Id).Single(rt => rt.TokenHash == CalcularHash(tokenB));
        Assert.Null(persistidoB.RevokedAtUtc);

        var sucessorSegueValido = await RefreshAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.OK, sucessorSegueValido.StatusCode);

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Reuso de refresh token detectado", StringComparison.Ordinal));
    }

    // ══════════════ D8 — disciplina de auditoria ══════════════

    [Fact]
    public async Task CorridaBenigna_LogaAuditoriaEmInformationComApenasOIdDoUsuario()
    {
        // D8: a corrida benigna precisa ser visível (é o insumo para decidir, no futuro, se vale
        // implementar a coordenação entre abas — P-03), mas em categoria e nível distintos do
        // reuso real. Se ela caísse em Warning, o sinal de segurança mais grave do sistema
        // voltaria a se diluir em ruído — que é o problema que a issue existe para resolver.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        var resposta = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var entrada = Assert.Single(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Corrida benigna de refresh token", StringComparison.Ordinal));

        Assert.Equal(LogEventLevel.Information, entrada.Level);
        Assert.Contains(
            "TccManager.Api.Auditoria",
            entrada.Properties["SourceContext"].ToString(),
            StringComparison.Ordinal);

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"usuário {usuario.Id}", mensagem, StringComparison.Ordinal);

        // Mesma disciplina de PII/segredo já aplicada aos demais logs de auditoria do projeto
        // (achado A09-2): só ids, nunca texto livre — e aqui, jamais o token em si.
        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains(tokenA, StringComparison.Ordinal) ||
                 e.RenderMessage().Contains(tokenB, StringComparison.Ordinal) ||
                 e.RenderMessage().Contains(CalcularHash(tokenA), StringComparison.Ordinal));

        // E o alarme de reuso continua calado quando não houve reuso.
        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Reuso de refresh token detectado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReusoReal_ContinuaLogandoWarningComApenasOIdDoUsuario()
    {
        // Contraparte do teste acima: o texto e o nível do evento da issue #62 são inalterados.
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var (usuario, tokenA, tokenB) = await LoginERotacaoAsync(factory, client);

        factory.Relogio.Avancar(JanelaPadrao + TimeSpan.FromSeconds(1));

        var reuso = await RefreshAsync(client, tokenA);
        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode);

        var entrada = Assert.Single(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Reuso de refresh token detectado", StringComparison.Ordinal));

        Assert.Equal(LogEventLevel.Warning, entrada.Level);
        Assert.Contains($"usuário {usuario.Id}", entrada.RenderMessage(), StringComparison.Ordinal);

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains(tokenA, StringComparison.Ordinal) ||
                 e.RenderMessage().Contains(tokenB, StringComparison.Ordinal) ||
                 e.RenderMessage().Contains(CalcularHash(tokenA), StringComparison.Ordinal));

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Corrida benigna de refresh token", StringComparison.Ordinal));
    }

    // ══════════════ o caminho quente não pode ter mudado ══════════════

    [Fact]
    public async Task RotacaoNormal_ContinuaEmitindoParNovoENaoLogaNadaDeCorrida()
    {
        // Controle: nada do ramo novo pode ser exercitado por um /refresh comum. O caminho feliz
        // sequer chega a ler o sucessor no banco (nota da seção 8.2).
        using var factory = new RefreshJanelaDeGracaApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);
        var usuario = await SemearUsuarioAsync(factory);

        var loginToken = await FazerLoginAsync(client);
        var (_, novoRefreshToken) = await RotacionarAsync(client, loginToken);

        Assert.NotEqual(loginToken, novoRefreshToken);
        Assert.Equal(2, TokensDoUsuario(factory, usuario.Id).Count);

        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Corrida benigna", StringComparison.Ordinal) ||
                 e.RenderMessage().Contains("Reuso de refresh token detectado", StringComparison.Ordinal));
    }
}
