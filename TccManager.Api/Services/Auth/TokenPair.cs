namespace TccManager.Api.Services.Auth;

/// <summary>
/// Issue #171 (migração do refresh token para cookie HttpOnly, D7 do documento de arquitetura
/// docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md). Substitui
/// <c>TccManager.Shared.DTOs.TokenPairDto</c> como tipo de retorno interno de
/// <see cref="IAuthTokenService"/> — só a API conhece este tipo, nunca é serializado no corpo de
/// uma resposta HTTP. Manter o valor bruto do refresh token acessível a partir de um tipo
/// referenciado pelo Client (como era com TokenPairDto em Shared) deixava um <c>return Ok(par)</c>
/// a um passo de reintroduzir o achado B9 (refresh token exposto a JavaScript).
/// </summary>
public class TokenPair
{
    public string Token { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Expiração real do refresh token no banco — usada por <c>RefreshTokenCookie</c> para
    /// calcular o <c>Max-Age</c> do cookie (D2), sem duplicar a leitura de
    /// <c>Jwt:RefreshTokenDays</c> fora de <see cref="AuthTokenService"/>.
    /// </summary>
    public DateTime RefreshTokenExpiresAtUtc { get; set; }
}
