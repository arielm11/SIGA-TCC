using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.Models;
using TccManager.Shared.DTOs;

[ApiController]
[Route("api/[controller]")]
public class UsuarioController : ControllerBase
{
    private readonly AppDbContext _context;

    public UsuarioController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsuarios()
    {
        var usuarios = await _context.Usuarios
            .Select(u => new UsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                Admin = u.Admin,
                Ativo = u.Ativo
            })
            .ToListAsync();

        return Ok(usuarios);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUsuarioById(int id)
    {
        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Admin = usuario.Admin,
            Ativo = usuario.Ativo
        };

        return Ok(usuario);
    }

    [HttpPost]
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
            Admin = dto.Admin,
            Ativo = true
        };

        _context.Usuarios.Add(newUsuario);
        await _context.SaveChangesAsync();

        return Ok(newUsuario);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUsuario(int id, [FromBody] UsuarioDto dto)
    {
        var usuario = await _context.Usuarios.FindAsync(id);
        
        if (usuario == null)
            return NotFound("Usuário não encontrado");
        
        usuario.Nome = dto.Nome;
        usuario.Email = dto.Email;
        usuario.Admin = dto.Admin;
        usuario.Ativo = dto.Ativo;
        
        if (!string.IsNullOrEmpty(dto.Senha))
            usuario.SenhaHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

        await _context.SaveChangesAsync();
        
        return Ok(usuario);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUsuario(int id)
    {
        var usuario = await _context.Usuarios.FindAsync(id);
        
        if (usuario == null)
            return NotFound("Usuário não encontrado");
        
        _context.Usuarios.Remove(usuario);
        await _context.SaveChangesAsync();
        
        return Ok("Usuário deletado com sucesso");
    }
}