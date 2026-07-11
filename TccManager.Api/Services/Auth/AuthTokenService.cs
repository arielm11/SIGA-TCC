using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Models;

namespace TccManager.Api.Services.Auth;

public class AuthTokenService : IAuthTokenService
{
    private readonly AppDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _configuration;

    public AuthTokenService(AppDbContext context, ITokenService tokenService, IConfiguration configuration)
    {
        _context = context;
        _tokenService = tokenService;
        _configuration = configuration;
    }

    private int RefreshTokenDays => _configuration.GetValue<int?>("Jwt:RefreshTokenDays") ?? 7;

    public async Task<TokenPairDto> LoginAsync(Usuario usuario)
    {
        await RevokeAllForUserAsync(usuario.Id);

        var (par, _) = CriarNovoPar(usuario);
        await _context.SaveChangesAsync();

        return par;
    }

    public async Task<TokenPairDto?> RefreshAsync(string refreshTokenBruto)
    {
        var hash = CalcularHash(refreshTokenBruto);

        var tokenAtual = await _context.RefreshTokens
            .Include(rt => rt.Usuario)
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash);

        if (tokenAtual == null || tokenAtual.Usuario == null || !EhUtilizavel(tokenAtual))
            return null;

        var (par, novoRefreshToken) = CriarNovoPar(tokenAtual.Usuario);

        tokenAtual.RevokedAtUtc = DateTime.UtcNow;
        tokenAtual.ReplacedByTokenHash = novoRefreshToken.TokenHash;

        await _context.SaveChangesAsync();

        return par;
    }

    public async Task LogoutAsync(string refreshTokenBruto)
    {
        var hash = CalcularHash(refreshTokenBruto);

        var token = await _context.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == hash);

        if (token == null || token.RevokedAtUtc != null)
            return;

        token.RevokedAtUtc = DateTime.UtcNow;
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
    private async Task RevokeAllForUserAsync(int usuarioId)
    {
        var agora = DateTime.UtcNow;

        var tokensAtivos = await _context.RefreshTokens
            .Where(rt => rt.UsuarioId == usuarioId && rt.RevokedAtUtc == null)
            .ToListAsync();

        foreach (var token in tokensAtivos)
        {
            token.RevokedAtUtc = agora;
        }
    }

    private (TokenPairDto Par, RefreshToken NovoRefreshToken) CriarNovoPar(Usuario usuario)
    {
        var (accessToken, expiresAtUtc) = _tokenService.GerarAccessToken(usuario);

        // CSPRNG (não Guid.NewGuid): o refresh token é uma credencial de portador de 7 dias,
        // precisa de garantia de imprevisibilidade criptográfica, não apenas unicidade.
        var refreshTokenBruto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var agora = DateTime.UtcNow;

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

    private static bool EhUtilizavel(RefreshToken token) =>
        token.RevokedAtUtc == null && token.ExpiresAtUtc > DateTime.UtcNow;

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
