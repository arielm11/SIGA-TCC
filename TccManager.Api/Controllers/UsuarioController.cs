using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Models;
using TccManager.Shared.Enums;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsuarioController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<UsuarioController> _logger;

    public UsuarioController(AppDbContext context, ILogger<UsuarioController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUsuarios()
    {
        var usuarios = await _context.Usuarios
            .Select(u => new UsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                Tipo = u.Tipo,
                Ativo = u.Ativo
            })
            .ToListAsync();

        return Ok(usuarios);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMeuPerfil()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("nameid")?.Value;

        if (string.IsNullOrEmpty(userIdClaim))
            return Unauthorized();

        var usuario = await _context.Usuarios.FindAsync(int.Parse(userIdClaim));

        if (usuario == null)
            return NotFound("Usuário não encontrado.");

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Tipo = usuario.Tipo,
            Ativo = usuario.Ativo
        };

        return Ok(usuarioDto);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUsuarioById(int id)
    {
        if (!PodeAcessarOuEditar(id))
        {
            _logger.LogWarning(
                "Acesso negado em GET /api/usuario/{{id}}. Solicitante: {SolicitanteId}, alvo: {AlvoId}",
                ObterIdClaimAutenticado() ?? "desconhecido",
                id);
            return Forbid();
        }

        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Tipo = usuario.Tipo,
            Ativo = usuario.Ativo
        };

        return Ok(usuarioDto);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUsuario([FromBody] UsuarioDto dto)
    {
        if(await _context.Usuarios.AnyAsync(u => u.Email == dto.Email))
            return BadRequest("Email já cadastrado");

        string passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

        var newUsuario = new Usuario
        {
            Nome = dto.Nome,
            Email = dto.Email,
            SenhaHash = passwordHash,
            Tipo = dto.Tipo,
            Ativo = true
        };

        _context.Usuarios.Add(newUsuario);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Não logar a exceção crua nem o email: o índice único de email pode
            // ser violado numa corrida com o pre-check acima, e a exceção do
            // provedor relacional traz o valor duplicado na mensagem (LGPD).
            // O "when" restringe o catch à violação de unicidade (2601/2627) —
            // outras causas de DbUpdateException (banco indisponível, outra
            // constraint) propagam em vez de virar um falso "email já em uso".
            _logger.LogWarning("Falha ao salvar usuário em POST /api/usuario: possível violação de restrição de unicidade de email.");
            return Conflict("Não foi possível salvar o usuário. Verifique se o email já está em uso.");
        }

        var novoUsuarioDto = new UsuarioDto
        {
            Id = newUsuario.Id,
            Nome = newUsuario.Nome,
            Email = newUsuario.Email,
            Tipo = newUsuario.Tipo,
            Ativo = newUsuario.Ativo
        };

        return Ok(novoUsuarioDto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUsuario(int id, [FromBody] UsuarioDto dto)
    {
        if (!PodeAcessarOuEditar(id))
        {
            _logger.LogWarning(
                "Acesso negado em PUT /api/usuario/{{id}}. Solicitante: {SolicitanteId}, alvo: {AlvoId}",
                ObterIdClaimAutenticado() ?? "desconhecido",
                id);
            return Forbid();
        }

        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        if (dto.Email != usuario.Email &&
            await _context.Usuarios.AnyAsync(u => u.Email == dto.Email && u.Id != id))
        {
            return BadRequest("Email já cadastrado");
        }

        usuario.Nome = dto.Nome;
        usuario.Email = dto.Email;

        // Tipo e Ativo sao campos sensiveis (escalacao de privilegio) e so podem
        // ser alterados por um Admin. Em autoedicao, o valor enviado pelo cliente
        // e ignorado e o valor atual do servidor e preservado.
        if (User.IsInRole("Admin"))
        {
            var tipoAntigo = usuario.Tipo;
            var ativoAntigo = usuario.Ativo;

            var eraUnicoAdminAtivo = tipoAntigo == TipoUsuario.Admin && ativoAntigo
                && (dto.Tipo != TipoUsuario.Admin || !dto.Ativo)
                && await EhUnicoAdminAtivoAsync();

            if (eraUnicoAdminAtivo)
            {
                _logger.LogWarning(
                    "Alteração de campos sensíveis bloqueada em PUT /api/usuario/{{id}}: alvo é o único Admin ativo do sistema. Admin solicitante: {AdminId}, alvo: {AlvoId}, Tipo solicitado: {TipoSolicitado}, Ativo solicitado: {AtivoSolicitado}",
                    ObterIdClaimAutenticado() ?? "desconhecido",
                    id,
                    dto.Tipo,
                    dto.Ativo);
            }
            else
            {
                usuario.Tipo = dto.Tipo;
                usuario.Ativo = dto.Ativo;

                if (tipoAntigo != usuario.Tipo || ativoAntigo != usuario.Ativo)
                {
                    _logger.LogWarning(
                        "Alteração de campos sensíveis em PUT /api/usuario/{{id}}. Admin: {AdminId}, alvo: {AlvoId}, Tipo: {TipoAntigo} -> {TipoNovo}, Ativo: {AtivoAntigo} -> {AtivoNovo}",
                        ObterIdClaimAutenticado() ?? "desconhecido",
                        id,
                        tipoAntigo,
                        usuario.Tipo,
                        ativoAntigo,
                        usuario.Ativo);
                }
            }
        }

        if (!string.IsNullOrEmpty(dto.Senha))
            usuario.SenhaHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Não logar a exceção crua nem o email: o índice único de email pode
            // ser violado numa corrida com o pre-check acima, e a exceção do
            // provedor relacional traz o valor duplicado na mensagem (LGPD).
            // O "when" restringe o catch à violação de unicidade (2601/2627) —
            // outras causas de DbUpdateException (banco indisponível, outra
            // constraint) propagam em vez de virar um falso "email já em uso".
            _logger.LogWarning(
                "Falha ao salvar usuário em PUT /api/usuario/{{id}}: possível violação de restrição de unicidade de email. Alvo: {AlvoId}",
                id);
            return Conflict("Não foi possível salvar o usuário. Verifique se o email já está em uso.");
        }

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Tipo = usuario.Tipo,
            Ativo = usuario.Ativo
        };

        return Ok(usuarioDto);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteUsuario(int id)
    {
        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        if (usuario.Tipo == TipoUsuario.Admin && usuario.Ativo && await EhUnicoAdminAtivoAsync())
            return BadRequest("Não é possível excluir o único Admin ativo do sistema.");

        // Professor orientando TCC ou compondo banca tem FK NO ACTION/Restrict para
        // usuarios: excluir sem checar antes derruba em DbUpdateException (500).
        var possuiVinculos = await _context.Tccs.AnyAsync(t => t.OrientadorId == id)
            || await _context.BancaAvaliadores.AnyAsync(b => b.ProfessorId == id);

        if (possuiVinculos)
            return Conflict("Não é possível excluir: o usuário orienta TCC(s) e/ou participa de banca(s) avaliadora(s).");

        _context.Usuarios.Remove(usuario);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Backstop para violação de integridade referencial não coberta pelas
            // checagens acima (ex.: vínculo futuro ainda não previsto aqui).
            _logger.LogWarning(
                "Falha ao excluir usuário em DELETE /api/usuario/{{id}}: possível violação de integridade referencial. Alvo: {AlvoId}",
                id);
            return Conflict("Não é possível excluir este usuário: existem registros vinculados a ele.");
        }

        return Ok("Usuário deletado com sucesso");
    }

    [HttpGet("professores")]
    [Authorize]
    public async Task<IActionResult> GetProfessores()
    {
        var professores = await _context.Usuarios
            .Where(u => u.Tipo == TipoUsuario.Professor && u.Ativo)
            .Select(u => new UsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                Tipo = u.Tipo,
                Ativo = u.Ativo
            })
            .ToListAsync();

        return Ok(professores);
    }

    // Autoriza a operacao se o usuario autenticado for Admin ou se estiver
    // acessando/editando o proprio registro (id da rota == id do proprio claim).
    private bool PodeAcessarOuEditar(int id)
    {
        if (User.IsInRole("Admin"))
            return true;

        var userIdClaim = ObterIdClaimAutenticado();

        return int.TryParse(userIdClaim, out var usuarioIdAutenticado)
            && usuarioIdAutenticado == id;
    }

    private string? ObterIdClaimAutenticado() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("nameid")?.Value;

    // Verifica se existe apenas 1 Admin ativo no sistema no momento da chamada.
    // Usado para impedir que a API deixe o sistema sem nenhum Admin ativo
    // (auto-degradacao/desativacao ou exclusao do ultimo Admin).
    private async Task<bool> EhUnicoAdminAtivoAsync()
    {
        var totalAdminsAtivos = await _context.Usuarios
            .CountAsync(u => u.Tipo == TipoUsuario.Admin && u.Ativo);

        return totalAdminsAtivos <= 1;
    }
}