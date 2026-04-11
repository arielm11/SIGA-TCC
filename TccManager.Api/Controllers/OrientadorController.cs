using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Professor")]
public class OrientadorController : ControllerBase
{
    private readonly AppDbContext _context;

    public OrientadorController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDaboard()
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var pendentes = await _context.Tccs
            .Include(t => t.Aluno)
            .Where(t => t.Status == StatusTcc.Pendente)
            .Select(t => new TccResumoDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                Resumo = t.Resumo,
                NomeAluno = t.Aluno != null ? t.Aluno.Nome : "Desconecido",
                DataCriacao = t.DataCriacao,
                Status = t.Status
            }).ToListAsync();

        var ativos = await _context.Tccs
            .Include(t => t.Aluno)
            .Where(t => t.OrientadorId == profId && (t.Status == StatusTcc.Aprovado || t.Status == StatusTcc.EmAndamento))
            .Select(t => new TccResumoDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                Resumo = t.Resumo,
                NomeAluno = t.Aluno != null ? t.Aluno.Nome : "Desconecido",
                DataCriacao = t.DataCriacao,
                Status = t.Status
            }).ToListAsync();

        var dashboard = new DashboardOrientadorDto
        {
            PropostasPendentes = pendentes,
            OrientandosAtivos = ativos
        };

        return Ok(dashboard);
    }

    [HttpPost("propostas/{id}/aprovar")]
    public async Task<IActionResult> AprovarProposta(int id)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == id && t.Status == StatusTcc.Pendente);

        if (tcc == null) return NotFound("Proposta não encontrada ou já avaliada.");
        
        tcc.Status = StatusTcc.Aprovado;
        tcc.OrientadorId = profId;

        await _context.SaveChangesAsync();
        return Ok("Proposta enviada com sucesso!");
    }

    [HttpPost("propostas/{id}/rejeitar")]
    public async Task<IActionResult> RejeitarProposta(int id, [FromBody] RejeicaoDto dto) 
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == id && t.Status == StatusTcc.Pendente);

        if (tcc == null) return NotFound("Proposta não encontrada ou já avaliada.");

        tcc.Status = StatusTcc.Reprovado;
        tcc.MotivoRejeicao = dto.Motivo;

        await _context.SaveChangesAsync();
        return Ok("Proposta rejeitada.");
    }
}
