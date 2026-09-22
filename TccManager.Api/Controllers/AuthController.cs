using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Configuration;
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
    private readonly ILogger<AuthController> _logger;

    // Categoria dedicada de auditoria (issue #88, D11) — mesmo padrão já estabelecido em
    // AuthTokenService/TccController/CoordenadorController/OrientadorController/
    // RascunhoAtaController.
    private readonly ILogger _auditLogger;

    public AuthController(
        AppDbContext context,
        IAuthTokenService authTokenService,
        ILogger<AuthController> logger,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _authTokenService = authTokenService;
        _logger = logger;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
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
        {
            // Nunca logar o e-mail tentado (decisão de LGPD registrada na issue #61).
            _logger.LogWarning(
                "Falha de login. IP de origem: {RemoteIp}, motivo: usuário não encontrado",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Usuário não encontrado.");
        }

        if (!BCrypt.Net.BCrypt.Verify(dto.Senha, usuario.SenhaHash))
        {
            _logger.LogWarning(
                "Falha de login. IP de origem: {RemoteIp}, motivo: senha incorreta",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Senha incorreta.");
        }

        if (!usuario.Ativo)
        {
            _logger.LogWarning(
                "Falha de login. IP de origem: {RemoteIp}, motivo: usuário inativo",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Usuário inativo.");
        }

        // Issue #88 (D8, RF-04): credenciais válidas, mas há uma troca de senha obrigatória
        // pendente (Admin criado por bootstrap). 403 (não 401: a autenticação é válida, falta
        // uma ação) e NENHUM token é emitido — a barreira é estrutural, não uma flag que o
        // cliente pode ignorar. StatusCode explícito (não Forbid(), que dispara o challenge
        // do esquema de autenticação).
        if (usuario.PrecisaTrocarSenha)
        {
            _logger.LogWarning(
                "Login bloqueado: troca de senha obrigatória pendente. IP de origem: {RemoteIp}",
                HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status403Forbidden, new PrecisaTrocarSenhaResponseDto());
        }

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
    [EnableRateLimiting("refresh")]
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

    // Issue #88 (D9): fecha o ciclo de RF-04. [AllowAnonymous], mas NÃO é a Opção B
    // disfarçada — não cria nada, não eleva privilégio, e exige a senha ATUAL válida (a
    // mesma prova de identidade que /login, que já é anônimo). Só funciona para quem já tem
    // PrecisaTrocarSenha ligada, e não existe caminho para ligá-la pela API — não é um
    // "esqueci minha senha" genérico. Política de rate limiting própria (RNF-02).
    [HttpPost("trocar-senha-obrigatoria")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.TrocaSenhaPolicyName)]
    public async Task<IActionResult> TrocarSenhaObrigatoria([FromBody] TrocarSenhaObrigatoriaDto dto)
    {
        var usuario = await _context.Usuarios
            .FirstOrDefaultAsync(u => u.Email == dto.Email);

        if (usuario == null || !usuario.Ativo || !BCrypt.Net.BCrypt.Verify(dto.SenhaAtual, usuario.SenhaHash))
        {
            // Nunca logar o e-mail tentado (decisão de LGPD, issue #61) — mesma mensagem
            // genérica para usuário inexistente/inativo/senha incorreta, para não virar um
            // oráculo de enumeração distinto do de /login.
            _logger.LogWarning(
                "Falha em POST /api/auth/trocar-senha-obrigatoria. IP de origem: {RemoteIp}, motivo: credenciais inválidas ou usuário inativo",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Credenciais inválidas.");
        }

        if (!usuario.PrecisaTrocarSenha)
        {
            _logger.LogWarning(
                "Falha em POST /api/auth/trocar-senha-obrigatoria. IP de origem: {RemoteIp}, motivo: não há troca de senha pendente. UsuarioId: {UsuarioId}",
                HttpContext.Connection.RemoteIpAddress,
                usuario.Id);
            return BadRequest("Não há troca de senha pendente para este usuário.");
        }

        // A política (mínimo, máximo, diferente do e-mail e da senha atual) já foi checada
        // por TrocarSenhaObrigatoriaDtoValidator (FluentValidationActionFilter, antes desta
        // action executar) — aqui só a escrita.
        usuario.SenhaHash = BCrypt.Net.BCrypt.HashPassword(dto.NovaSenha);
        usuario.PrecisaTrocarSenha = false;

        await _context.SaveChangesAsync();

        // RF-05 (A5): fecha o ciclo — prova que a credencial de bootstrap deixou de ser
        // válida. Information porque é o desfecho esperado, não uma anomalia.
        _auditLogger.LogInformation(
            "Troca de senha obrigatória concluída para usuário {UsuarioId}.",
            usuario.Id);

        return NoContent();
    }
}
