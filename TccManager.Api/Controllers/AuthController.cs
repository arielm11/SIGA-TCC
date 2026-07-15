using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Api.Services.Auth;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAuthTokenService _authTokenService;

    public AuthController(AppDbContext context, IAuthTokenService authTokenService)
    {
        _context = context;
        _authTokenService = authTokenService;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var usuario = await _context.Usuarios
            .FirstOrDefaultAsync(u => u.Email == dto.Email);

        // Decisão de produto (2026-07-14): mensagens diferenciadas por campo, aceitando o risco
        // de enumeração de usuários (um atacante pode descobrir quais e-mails têm conta testando
        // o login) em troca de um erro mais claro para o usuário legítimo. Reverte a postura
        // anti-enumeração anterior deliberadamente — não é uma regressão não avaliada.
        if (usuario == null)
            return Unauthorized("Usuário não encontrado.");

        if (!BCrypt.Net.BCrypt.Verify(dto.Senha, usuario.SenhaHash))
            return Unauthorized("Senha incorreta.");

        if (!usuario.Ativo)
            return Unauthorized("Usuário inativo.");

        var par = await _authTokenService.LoginAsync(usuario);

        return Ok(new LoginResponseDto
        {
            Token = par.Token,
            RefreshToken = par.RefreshToken,
            Nome = usuario.Nome,
            Email = usuario.Email
        });
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.RefreshToken))
            return Unauthorized();

        var par = await _authTokenService.RefreshAsync(dto.RefreshToken);

        if (par == null)
            return Unauthorized();

        return Ok(par);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequestDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.RefreshToken))
            await _authTokenService.LogoutAsync(dto.RefreshToken);

        return NoContent();
    }
}
