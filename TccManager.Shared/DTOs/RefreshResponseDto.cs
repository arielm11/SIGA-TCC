namespace TccManager.Shared.DTOs;

// Issue #171: resposta de POST /api/auth/refresh depois da migração do refresh token para
// cookie HttpOnly — só o access token volta no corpo; o sucessor do refresh token é entregue via
// Set-Cookie. Ver docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, seção 7.1/D7.
public class RefreshResponseDto
{
    public string Token { get; set; } = string.Empty;
}
