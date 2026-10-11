namespace TccManager.Shared.DTOs;

// Issue #171: o refresh token deixou de viajar no corpo JSON (RF02) — passa a ser entregue
// exclusivamente via Set-Cookie HttpOnly em POST /api/auth/login. Ver
// docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, seção 7.1/D7.
public class LoginResponseDto
{
    public string Token { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
