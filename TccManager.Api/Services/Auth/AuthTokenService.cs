using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
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

    public async Task<TokenPairDto> LoginAsync(Usuario usuario)
    {
        var agora = _timeProvider.GetUtcNow().UtcDateTime;

        await RevokeAllForUserAsync(usuario.Id, agora);

        var (par, _) = CriarNovoPar(usuario, agora);
        await _context.SaveChangesAsync();

        return par;
    }

    public async Task<TokenPairDto?> RefreshAsync(string refreshTokenBruto)
    {
        var hash = CalcularHash(refreshTokenBruto);
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
            return await TratarTokenRotacionadoReapresentadoAsync(tokenAtual, hash, agora);
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
    /// Classifica a reapresentação de um token já rotacionado: corrida benigna (replay
    /// idempotente do par vigente) ou reuso real (reuse-detection integral, inalterada).
    /// Ver D2 e a seção 5.2 do documento de arquitetura para o desenho completo.
    /// </summary>
    private async Task<TokenPairDto?> TratarTokenRotacionadoReapresentadoAsync(
        RefreshToken tokenAtual, string hashApresentado, DateTime agora)
    {
        var janelaSegundos = RefreshReuseGraceSeconds;

        // janelaSegundos <= 0 precisa produzir dentroDaJanela = false SEMPRE, mesmo se
        // RevokedAtUtc == agora (relógio congelado em teste, ou timing real coincidente):
        // é isso que faz "0" significar "estritamente o comportamento da issue #62".
        var dentroDaJanela = janelaSegundos > 0
            && (agora - tokenAtual.RevokedAtUtc!.Value) <= TimeSpan.FromSeconds(janelaSegundos);

        RefreshToken? sucessor = null;
        if (dentroDaJanela)
        {
            sucessor = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenAtual.ReplacedByTokenHash);
        }

        var sucessorEhPontaAtiva = sucessor != null
            && sucessor.RevokedAtUtc == null
            && sucessor.ExpiresAtUtc > agora;

        if (!(dentroDaJanela && sucessorEhPontaAtiva))
        {
            _logger.LogWarning(
                "Reuso de refresh token detectado para usuário {UsuarioId}, IP de origem: {IpOrigem} — sessão revogada por precaução",
                tokenAtual.UsuarioId, IpDeOrigem);

            await RevokeAllForUserAsync(tokenAtual.UsuarioId, agora);
            await _context.SaveChangesAsync();

            return null;
        }

        // ── corrida benigna a partir daqui — nenhuma escrita no banco neste caminho ──

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
        if (!_cache.TryGetValue(ChaveReplay(hashApresentado), out string? sucessorBruto) ||
            string.IsNullOrEmpty(sucessorBruto))
        {
            _auditLogger.LogInformation(
                "Corrida benigna de refresh detectada para usuário {UsuarioId}, mas sem entrada de replay em cache — recusando por segurança",
                tokenAtual.UsuarioId);
            return null;
        }

        var (accessToken, expiresAtUtc) = _tokenService.GerarAccessToken(tokenAtual.Usuario);

        // Achado A09-1 da revisão de segurança: dentro da janela de graça, este é o único
        // controle compensatório que sobra (a reuse-detection deliberadamente não dispara) —
        // sem IP não dá para depois responder "o replay veio do mesmo cliente que rotacionou,
        // ou de outra origem?".
        _auditLogger.LogInformation(
            "Corrida benigna de refresh token para usuário {UsuarioId}, IP de origem: {IpOrigem} — replay idempotente do par vigente",
            tokenAtual.UsuarioId, IpDeOrigem);

        return new TokenPairDto
        {
            Token = accessToken,
            RefreshToken = sucessorBruto,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public async Task LogoutAsync(string refreshTokenBruto)
    {
        var hash = CalcularHash(refreshTokenBruto);

        var token = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash);

        if (token == null || token.RevokedAtUtc != null)
            return;

        token.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
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

    private (TokenPairDto Par, RefreshToken NovoRefreshToken) CriarNovoPar(Usuario usuario, DateTime agora)
    {
        var (accessToken, expiresAtUtc) = _tokenService.GerarAccessToken(usuario);

        // CSPRNG (não Guid.NewGuid): o refresh token é uma credencial de portador de 7 dias,
        // precisa de garantia de imprevisibilidade criptográfica, não apenas unicidade.
        var refreshTokenBruto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var novoRefreshToken = new RefreshToken
        {
            UsuarioId = usuario.Id,
            TokenHash = CalcularHash(refreshTokenBruto),
            CreatedAtUtc = agora,
            ExpiresAtUtc = agora.AddDays(RefreshTokenDays)
        };

        _context.RefreshTokens.Add(novoRefreshToken);

        var par = new TokenPairDto
        {
            Token = accessToken,
            RefreshToken = refreshTokenBruto,
            ExpiresAtUtc = expiresAtUtc
        };

        return (par, novoRefreshToken);
    }

    private static bool EhUtilizavel(RefreshToken token, DateTime agora) =>
        token.RevokedAtUtc == null && token.ExpiresAtUtc > agora;

    /// <summary>Chave de cache do replay idempotente (D4) — TTL = janela de graça.</summary>
    private static string ChaveReplay(string tokenHash) => $"refresh-replay:{tokenHash}";

    /// <summary>
    /// SHA-256 em hex, sempre minúsculo — garante comparação confiável de igualdade em
    /// <c>TokenHash</c> independentemente da collation do banco (ver docs/dados).
    /// </summary>
    private static string CalcularHash(string valor)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(valor));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
