using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public UsuarioController(AppDbContext context)
    {
        _context = context;
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
            return Forbid();

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
        await _context.SaveChangesAsync();

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
            return Forbid();

        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        usuario.Nome = dto.Nome;
        usuario.Email = dto.Email;

        // Tipo e Ativo sao campos sensiveis (escalacao de privilegio) e so podem
        // ser alterados por um Admin. Em autoedicao, o valor enviado pelo cliente
        // e ignorado e o valor atual do servidor e preservado.
        if (User.IsInRole("Admin"))
        {
            usuario.Tipo = dto.Tipo;
            usuario.Ativo = dto.Ativo;
        }

        if (!string.IsNullOrEmpty(dto.Senha))
            usuario.SenhaHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

        await _context.SaveChangesAsync();

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

        _context.Usuarios.Remove(usuario);
        await _context.SaveChangesAsync();

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

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("nameid")?.Value;

        return int.TryParse(userIdClaim, out var usuarioIdAutenticado)
            && usuarioIdAutenticado == id;
    }
}