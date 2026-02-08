using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TccController : ControllerBase
{
    private readonly AppDbContext _context;

    public TccController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost("proposta")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> SubmeterProposta([FromBody] PropostaTccDto dto)
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (alunoIdClaim == null || !int.TryParse(alunoIdClaim.Value, out int alunoId))
        {
            return Unauthorized("Usuário não identificado.");
        }

        var existeTccAtivo = await _context.Tccs
            .AnyAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);

        if (existeTccAtivo)
        {
            return BadRequest("Você já possui uma proposta de TCC ativa ou em avaliação.");
        }

        var novoTcc = new Tcc
        {
            Titulo = dto.Titulo,
            Resumo = dto.Resumo,
            AlunoId = alunoId,
            Status = StatusTcc.Pendente,
            DataCriacao = DateTime.UtcNow
        };

        _context.Tccs.Add(novoTcc);
        await _context.SaveChangesAsync();

        return Ok(novoTcc);
    }

    [HttpGet("meu-tcc")]
    public async Task<IActionResult> GetMeuTcc()
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (alunoIdClaim == null || !int.TryParse(alunoIdClaim.Value, out int alunoId)) return Unauthorized();

        var tcc = await _context.Tccs
            .FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);

        if (tcc == null) return NoContent();

        return Ok(tcc);
    }
}