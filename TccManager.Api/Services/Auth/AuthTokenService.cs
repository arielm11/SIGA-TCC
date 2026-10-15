using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Api.Services.Auth;

public class AuthTokenService : IAuthTokenService
{
    private readonly AppDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IMemoryCache _cache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthTokenService> _logger;

    // Categoria dedicada de auditoria: mesmo padrão de TccController/CoordenadorController/
    // OrientadorController/RascunhoAtaController. O MinimumLevel padrão do Serilog é Warning
    // (descartaria Information), mas "TccManager.Api.Auditoria" tem Override explícito em
    // appsettings.json. Usada aqui para a corrida benigna de refresh (issue #85) — precisa
    // ficar visível, mas em categoria distinta do Warning de reuso real (issue #62), senão o
    // sinal de segurança mais grave do sistema volta a se diluir em ruído.
    private readonly ILogger _auditLogger;

    public AuthTokenService(
        AppDbContext context,
        ITokenService tokenService,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthTokenService> logger,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _tokenService = tokenService;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _cache = cache;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
    }

    private int RefreshTokenDays => _configuration.GetValue<int?>("Jwt:RefreshTokenDays") ?? 7;

    // Issue #85: janela de graça para reapresentação de um refresh token já rotacionado.
    // 0 desliga a janela e restaura o comportamento estrito da issue #62 (reversão sem
    // redeploy de código) — ver docs/arquitetura/2026-09-03-reuse-detection-falso-positivo-multi-aba.md, D5/5.3.
    // Achado A02-1 da revisão de segurança: sem limite superior, um valor configurado
    // absurdamente alto (ex.: 604800) tornaria a condição de "sucessor ainda é a ponta ativa"
    // a única defesa real (a própria arquitetura, §6.2, já registra que isso sozinho "seria
    // péssimo"). Clamp para a faixa defensável que o documento de arquitetura avaliou (0-60s).
    private int RefreshReuseGraceSeconds =>
        Math.Clamp(_configuration.GetValue<int?>("Jwt:RefreshReuseGraceSeconds") ?? 30, 0, 60);

    private string? IpDeOrigem => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public async Task<TokenPair> LoginAsync(Usuario usuario)
    {
        var agora = _timeProvider.GetUtcNow().UtcDateTime;

        await RevokeAllForUserAsync(usuario.Id, agora);

        var (par, _) = CriarNovoPar(usuario, agora);
        await _context.SaveChangesAsync();

        return par;
    }

    public async Task<TokenPair?> RefreshAsync(string refreshTokenBruto)
    {
        var hash = OpaqueTokenHelper.CalcularHash(refreshTokenBruto);
        var agora = _timeProvider.GetUtcNow().UtcDateTime;

        var tokenAtual = await _context.RefreshTokens
            .Include(rt => rt.Usuario)
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash);

        if (tokenAtual == null)
            return null;

        // Ramo de token revogado POR ROTAÇÃO: hoje sempre acusa reuso (issue #62). A
        // reapresentação de um token já rotacionado também acontece em cenários benignos
        // (corrida entre abas, resposta perdida por queda de rede, exceção não tratada no
        // cliente, F5 durante o /refresh em voo) — ver seção 2.2 do documento de arquitetura.
        // Só entra aqui quando RevokedAtUtc E ReplacedByTokenHash estão preenchidos: logout e
        // login preenchem só RevokedAtUtc, o que já distingue rotação de revogação manual.
        if (tokenAtual.RevokedAtUtc != null && tokenAtual.ReplacedByTokenHash != null)
        {
            return await TratarTokenRotacionadoReapresentadoAsync(tokenAtual, agora);
        }

        if (tokenAtual.Usuario == null || !tokenAtual.Usuario.Ativo || !EhUtilizavel(tokenAtual, agora))
            return null;

        var (par, novoRefreshToken) = CriarNovoPar(tokenAtual.Usuario, agora);

        tokenAtual.RevokedAtUtc = agora;
        tokenAtual.ReplacedByTokenHash = novoRefreshToken.TokenHash;

        await _context.SaveChangesAsync();

        // Guarda o sucessor bruto para um eventual replay idempotente dentro da janela de
        // graça (D3/D4). Só depois do SaveChangesAsync bem-sucedido: um resíduo de cache para
        // uma rotação que não persistiu devolveria um refresh token inativo no banco, que
        // falharia no uso seguinte de qualquer forma — mas gravar depois é trivialmente mais
        // correto. TTL 0 (janela desligada) não é um valor válido para o cache, por isso o guard.
        if (RefreshReuseGraceSeconds > 0)
        {
            _cache.Set(ChaveReplay(hash), par.RefreshToken, TimeSpan.FromSeconds(RefreshReuseGraceSeconds));
        }

        return par;
    }

    /// <summary>
    /// Limite de saltos que <see cref="TratarTokenRotacionadoReapresentadoAsync"/> caminha na
    /// cadeia de rotações antes de desistir e tratar como reuso. Achado L1 da revisão de
    /// segurança de #171 (ver seção 6.5 do documento de arquitetura
    /// <c>docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md</c>): com 3+ abas, o
    /// sucessor imediato do token reapresentado pode já não ser mais a ponta ativa (outra aba
    /// rotacionou de novo nesse meio-tempo) sem que isso seja reuso real. "N pequeno" por design:
    /// cada salto já precisa caber dentro da janela de graça (ver
    /// <see cref="RefreshReuseGraceSeconds"/>), então este limite só existe para não deixar a
    /// combinação "muitos saltos, cada um dentro da janela" se tornar, na prática, uma forma de a
    /// janela de graça nunca expirar (uma cadeia de N saltos pode somar até N × janela).
    /// </summary>
    private const int MaxSaltosCadeiaReplay = 5;

    /// <summary>
    /// Classifica a reapresentação de um token já rotacionado: corrida benigna (replay
    /// idempotente do par vigente, agora reconhecendo uma cadeia de rotações — não só um salto)
    /// ou reuso real (reuse-detection integral, inalterada). Ver seção 6.5 do documento de
    /// arquitetura para o desenho completo (achado L1 da revisão de segurança de #171).
    /// </summary>
    private async Task<TokenPair?> TratarTokenRotacionadoReapresentadoAsync(
        RefreshToken tokenAtual, DateTime agora)
    {
        var janelaSegundos = RefreshReuseGraceSeconds;

        // janelaSegundos <= 0 precisa recusar SEMPRE, mesmo se RevokedAtUtc == agora (relógio
        // congelado em teste, ou timing real coincidente): é isso que faz "0" significar
        // "estritamente o comportamento da issue #62", sem nenhuma tentativa de reconhecer cadeia.
        if (janelaSegundos <= 0)
            return await RevogarPorReusoAsync(tokenAtual, agora);

        var janela = TimeSpan.FromSeconds(janelaSegundos);

        // Caminha a cadeia de rotações a partir do token reapresentado (elo) até achar a ponta
        // ativa. Cada salto só é aceito se ocorreu dentro da janela de graça CONTADA A PARTIR DO
        // INSTANTE EM QUE O ELO ANTERIOR FOI ROTACIONADO (não da rotação original até agora) —
        // é essa comparação salto a salto, e não "agora - revogação original <= janela", que
        // impede que uma cadeia de muitos saltos rápidos vire uma janela de graça sem fim.
        var elo = tokenAtual;

        for (var salto = 0; salto < MaxSaltosCadeiaReplay; salto++)
        {
            if (elo.ReplacedByTokenHash == null)
            {
                // Revogado sem sucessor no meio da cadeia (logout/revogação manual): a sessão
                // foi legitimamente encerrada, não há par vigente para repetir.
                return await RevogarPorReusoAsync(tokenAtual, agora);
            }

            var proximo = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == elo.ReplacedByTokenHash);

            if (proximo == null)
                return await RevogarPorReusoAsync(tokenAtual, agora);

            // Se "proximo" ainda não foi rotacionado, o limite superior do salto é "agora"
            // (equivalente ao caso de 1 salto de antes). Se já foi, o limite é o instante em que
            // ELE foi rotacionado — o salto tem que ter sido rápido, não a reapresentação atual.
            var limiteDoSalto = proximo.RevokedAtUtc ?? agora;
            var saltoDentroDaJanela = (limiteDoSalto - elo.RevokedAtUtc!.Value) <= janela;

            if (!saltoDentroDaJanela)
                return await RevogarPorReusoAsync(tokenAtual, agora);

            if (proximo.RevokedAtUtc == null)
            {
                if (proximo.ExpiresAtUtc <= agora)
                    return await RevogarPorReusoAsync(tokenAtual, agora);

                // ── corrida benigna — nenhuma escrita no banco neste caminho ──
                return MontarReplayIdempotente(tokenAtual, elo, proximo);
            }

            elo = proximo;
        }

        // Cadeia maior que o limite defensável: mesma postura conservadora de "sucessor não é a
        // ponta ativa" — trata como reuso.
        return await RevogarPorReusoAsync(tokenAtual, agora);
    }

    /// <summary>Reuse-detection integral da issue #62: revoga todas as sessões do usuário.</summary>
    private async Task<TokenPair?> RevogarPorReusoAsync(RefreshToken tokenAtual, DateTime agora)
    {
        _logger.LogWarning(
            "Reuso de refresh token detectado para usuário {UsuarioId}, IP de origem: {IpOrigem} — sessão revogada por precaução",
            tokenAtual.UsuarioId, IpDeOrigem);

        await RevokeAllForUserAsync(tokenAtual.UsuarioId, agora);
        await _context.SaveChangesAsync();

        return null;
    }

    /// <summary>
    /// Monta a resposta de replay idempotente depois que a cadeia foi validada como benigna.
    /// <paramref name="penultimo"/> é o elo imediatamente anterior à ponta ativa
    /// (<paramref name="pontaAtiva"/>) — no caso de 1 salto, é o próprio <paramref name="tokenAtual"/>.
    /// O bruto devolvido vem do cache gravado quando ESSE salto (penúltimo → ponta ativa)
    /// aconteceu, não do salto do token originalmente reapresentado: numa cadeia de N saltos, só
    /// aquele cache tem o valor bruto da ponta ativa atual. Síncrono de propósito: nenhum caminho
    /// aqui toca o banco (D7/D6) — só <see cref="RevogarPorReusoAsync"/> escreve.
    /// </summary>
    private TokenPair? MontarReplayIdempotente(
        RefreshToken tokenAtual, RefreshToken penultimo, RefreshToken pontaAtiva)
    {
        // D7: o replay emite um access token novo. Sem esta guarda aqui (e não só no caminho
        // normal de rotação), a issue #59 (usuário desativado não deveria renovar) regride
        // pela porta dos fundos.
        if (tokenAtual.Usuario == null || !tokenAtual.Usuario.Ativo)
        {
            _auditLogger.LogInformation(
                "Corrida benigna de refresh recusada para usuário {UsuarioId}: usuário inativo",
                tokenAtual.UsuarioId);
            return null;
        }

        // D6: fail-safe de cache frio (restart da API, ou — no futuro — múltiplas instâncias
        // sem afinidade). Nunca cai na revogação em massa por isso; apenas recusa a chamada.
        if (!_cache.TryGetValue(ChaveReplay(penultimo.TokenHash), out string? sucessorBruto) ||
            string.IsNullOrEmpty(sucessorBruto))
        {
            _auditLogger.LogInformation(
                "Corrida benigna de refresh detectada para usuário {UsuarioId}, mas sem entrada de replay em cache — recusando por segurança",
                tokenAtual.UsuarioId);
            return null;
        }

        var (accessToken, _) = _tokenService.GerarAccessToken(tokenAtual.Usuario);

        // Achado A09-1 da revisão de segurança: dentro da janela de graça, este é o único
        // controle compensatório que sobra (a reuse-detection deliberadamente não dispara) —
        // sem IP não dá para depois responder "o replay veio do mesmo cliente que rotacionou,
        // ou de outra origem?".
        _auditLogger.LogInformation(
            "Corrida benigna de refresh token para usuário {UsuarioId}, IP de origem: {IpOrigem} — replay idempotente do par vigente",
            tokenAtual.UsuarioId, IpDeOrigem);

        return new TokenPair
        {
            Token = accessToken,
            RefreshToken = sucessorBruto,
            RefreshTokenExpiresAtUtc = pontaAtiva.ExpiresAtUtc
        };
    }

    public async Task LogoutAsync(string refreshTokenBruto)
    {
        var hash = OpaqueTokenHelper.CalcularHash(refreshTokenBruto);

        var token = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash);

        if (token == null || token.RevokedAtUtc != null)
            return;

        token.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync();
    }

    public async Task RevokeAllSessionsAsync(int usuarioId)
    {
        await RevokeAllForUserAsync(usuarioId, _timeProvider.GetUtcNow().UtcDateTime);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Revogação em massa (RF04), usada internamente pelo login. O documento de dados
    /// recomenda <c>ExecuteUpdateAsync</c> (set-based, sem materializar entidades); na
    /// prática, o provider EF Core InMemory usado pela suíte de testes de integração
    /// deste projeto (<c>TccApiFactory</c>) não suporta <c>ExecuteUpdateAsync</c>/
    /// <c>ExecuteDeleteAsync</c> (lança <see cref="InvalidOperationException"/>), o que
    /// quebraria o login em todo teste de integração existente. Optei por carregar as
    /// entidades ativas e atualizá-las via change tracker — ainda 100% EF Core, apenas
    /// sem o SQL set-based — coerente com o próprio documento de dados, que já registra
    /// que o volume de linhas por usuário é baixíssimo neste projeto. Fica registrado
    /// como decisão de implementação para revisão, caso o volume real cresça.
    /// </summary>
    private async Task RevokeAllForUserAsync(int usuarioId, DateTime agora)
    {
        var tokensAtivos = await _context.RefreshTokens
            .Where(rt => rt.UsuarioId == usuarioId && rt.RevokedAtUtc == null)
            .ToListAsync();

        foreach (var token in tokensAtivos)
        {
            token.RevokedAtUtc = agora;
        }
    }

    private (TokenPair Par, RefreshToken NovoRefreshToken) CriarNovoPar(Usuario usuario, DateTime agora)
    {
        var (accessToken, _) = _tokenService.GerarAccessToken(usuario);

        var refreshTokenBruto = OpaqueTokenHelper.GerarTokenBruto();

        var novoRefreshToken = new RefreshToken
        {
            UsuarioId = usuario.Id,
            TokenHash = OpaqueTokenHelper.CalcularHash(refreshTokenBruto),
            CreatedAtUtc = agora,
            ExpiresAtUtc = agora.AddDays(RefreshTokenDays)
        };

        _context.RefreshTokens.Add(novoRefreshToken);

        var par = new TokenPair
        {
            Token = accessToken,
            RefreshToken = refreshTokenBruto,
            RefreshTokenExpiresAtUtc = novoRefreshToken.ExpiresAtUtc
        };

        return (par, novoRefreshToken);
    }

    private static bool EhUtilizavel(RefreshToken token, DateTime agora) =>
        token.RevokedAtUtc == null && token.ExpiresAtUtc > agora;

    /// <summary>Chave de cache do replay idempotente (D4) — TTL = janela de graça.</summary>
    private static string ChaveReplay(string tokenHash) => $"refresh-replay:{tokenHash}";
}
