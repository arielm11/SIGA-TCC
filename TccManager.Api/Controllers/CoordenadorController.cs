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
[Authorize(Roles = "Coordenador")]
public class CoordenadorController : ControllerBase
{
    private readonly AppDbContext _context;

    public CoordenadorController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("dashboard-stats")]
    public async Task<IActionResult> GetDashboardStats()
    {
        var stats = new DashboardCoordenadorDto
        {
            TotalAtivos = await _context.Tccs.CountAsync(t => t.Status == StatusTcc.Aprovado || t.Status == StatusTcc.EmAndamento),
            AguardandoBanca = await _context.Tccs.CountAsync(t => t.Status == StatusTcc.AguardandoDefesa),
            PropostasPendentes = await _context.Tccs.CountAsync(t => t.Status == StatusTcc.Pendente),
            TccsConcluidos = await _context.Tccs.CountAsync(t => t.Status == StatusTcc.Finalizado)
        };

        return Ok(stats);
    }

    [HttpGet("professores")]
    public async Task<IActionResult> GetProfessores()
    {
        var professores = await _context.Usuarios
            .Where(u => u.Tipo == TipoUsuario.Professor && u.Ativo)
            .Select(u => new ProfessorResumoDto
            {
                Id = u.Id,
                Nome = u.Nome,
                LimiteOrientandos = u.LimiteOrientandos,
                AceitandoOrientandos = u.AceitandoOrientandos,
                CargaAtual = _context.Tccs.Count(t => t.OrientadorId == u.Id && (t.Status == StatusTcc.Aprovado || t.Status == StatusTcc.EmAndamento))
            })
            .OrderBy(p => p.Nome)
            .ToListAsync();

        return Ok(professores);
    }

    [HttpGet("propostas-pendentes")]
    public async Task<IActionResult> GetPropostasPendentes()
    {
        var pendentes = await _context.Tccs
            .Include(t => t.Aluno)
            .Where(t => t.Status == StatusTcc.Pendente)
            .Select(t => new TccResumoDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                NomeAluno = t.Aluno!.Nome,
                DataCriacao = t.DataCriacao,
                Status = t.Status
            }).ToListAsync();

        return Ok(pendentes);
    }

    [HttpPut("propostas/{id}/designar-orientador")]
    public async Task<IActionResult> DesignarOrientador(int id, [FromBody] DesignarOrientadorDto dto)
    {
        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == id && t.Status == StatusTcc.Pendente);
        if (tcc == null) return NotFound("Proposta não encontrada ou já processada.");

        var professorExiste = await _context.Usuarios.AnyAsync(u => u.Id == dto.OrientadorId && u.Tipo == TipoUsuario.Professor);
        if (!professorExiste) return BadRequest("Professor inválido.");

        tcc.OrientadorId = dto.OrientadorId;
        tcc.Status = StatusTcc.Aprovado;

        await _context.SaveChangesAsync();
        return Ok("Orientador designado com sucesso.");
    }

    [HttpPut("professores/{id}/capacidade")]
    public async Task<IActionResult> AtualizarCapacidade(int id, [FromBody] CapacidadeProfessorDto dto)
    {
        var professor = await _context.Usuarios.FirstOrDefaultAsync(u => u.Id == id && u.Tipo == TipoUsuario.Professor);
        if (professor == null) return NotFound("Professor não encontrado.");

        professor.LimiteOrientandos = dto.LimiteOrientandos;
        professor.AceitandoOrientandos = dto.AceitandoOrientandos;
        
        await _context.SaveChangesAsync();
        return Ok("Capacidade do professor atualizada com sucesso.");
    }

    // --- CRUD MEMBROS EXTERNOS ---
    [HttpGet("membros-externos")]
    public async Task<IActionResult> GetMembrosExternos()
    {
        var membros = await _context.MembrosExternos.OrderBy(m => m.Nome).ToListAsync();
        return Ok(membros);
    }

    [HttpPost("membros-externos")]
    public async Task<IActionResult> AdicionarMembroExterno([FromBody] MembroExterno membro)
    {
        _context.MembrosExternos.Add(membro);
        await _context.SaveChangesAsync();
        return Ok(membro);
    }

    [HttpPut("membros-externos/{id}")]
    public async Task<IActionResult> AtualizarMembroExterno(int id, [FromBody] MembroExternoDto dto)
    {
        var membro = await _context.MembrosExternos.FindAsync(id);

        if (membro == null)
            return NotFound("Membro externo não encontrado");

        membro.Nome = dto.Nome;
        membro.Email = dto.Email;
        membro.Instituicao = dto.Instituicao;

        await _context.SaveChangesAsync();

        return Ok(membro);
    }

    [HttpDelete("membros-externos/{id}")]
    public async Task<IActionResult> RemoverMembroExterno(int id)
    {
        var membro = await _context.MembrosExternos.FindAsync(id);
        if (membro == null) return NotFound("Membro externo não encontrado.");

        _context.MembrosExternos.Remove(membro);
        await _context.SaveChangesAsync();
        return Ok("Membro externo removido com sucesso.");
    }
}