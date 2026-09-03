using TccManager.Shared.DTOs;
using TccManager.Shared.Models;

namespace TccManager.Api.Services.Auth;

/// <summary>
/// Fachada de sessão consumida pelo <c>AuthController</c>: login (emissão + revogação de
/// sessões antigas), refresh (validação + rotação) e logout (revogação). Persistência via
/// EF Core, conforme decidido em docs/dados/2026-07-10-refresh-token-sessao.md.
/// </summary>
public interface IAuthTokenService
{
    /// <summary>
    /// Revoga todos os refresh tokens ativos do usuário (sessão única — RF04) e emite um
    /// novo par (JWT + refresh token).
    /// </summary>
    Task<TokenPairDto> LoginAsync(Usuario usuario);

    /// <summary>
    /// Valida o refresh token informado (existe, não revogado, não expirado). Em caso
    /// válido, rotaciona (revoga o apresentado, emite um novo) e retorna o novo par.
    /// Retorna <c>null</c> se o refresh token for inválido/expirado/revogado.
    ///
    /// Issue #85: dentro de <c>Jwt:RefreshReuseGraceSeconds</c> após a rotação, reapresentar
    /// o token recém-substituído deixa de ser tratado automaticamente como reuso — se o
    /// sucessor ainda for a ponta ativa da cadeia, é classificado como corrida benigna
    /// (multi-aba, resposta perdida, F5 em voo) e responde com um replay idempotente do
    /// mesmo par (o mesmo <c>RefreshToken</c> já emitido + um <c>Token</c> novo), sem
    /// revogar nada. Fora da janela, ou se o sucessor já não for a ponta ativa, o
    /// comportamento é o de sempre: reuse-detection integral, revoga todas as sessões do
    /// usuário. Ver docs/arquitetura/2026-09-03-reuse-detection-falso-positivo-multi-aba.md.
    /// </summary>
    Task<TokenPairDto?> RefreshAsync(string refreshTokenBruto);

    /// <summary>
    /// Revoga o refresh token informado (RF05). Idempotente: token inexistente ou já
    /// revogado não gera erro.
    /// </summary>
    Task LogoutAsync(string refreshTokenBruto);
}
