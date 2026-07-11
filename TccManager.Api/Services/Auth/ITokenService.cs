using TccManager.Shared.Models;

namespace TccManager.Api.Services.Auth;

/// <summary>
/// Responsável exclusivamente pela emissão/assinatura do JWT de acesso.
/// Lê a expiração de <c>Jwt:AccessTokenMinutes</c> (default 15).
/// </summary>
public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) GerarAccessToken(Usuario usuario);
}
