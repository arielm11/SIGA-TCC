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

    [HttpPost("tcc/{id}/banca")]
    public async Task<IActionResult> AgendarBanca(int idTcc, [FromBody] AgendarBancaDto dto)
    {
        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == idTcc);
        if (tcc == null || tcc.Status != StatusTcc.AguardandoDefesa)
            return BadRequest("O TCC deve estar com status 'Aguardando Defesa' para agendar a banca.");

        int totalMembros = dto.ProfessoresIds.Count + dto.MembrosExternosIds.Count;
        if (totalMembros < 2)
            return BadRequest("A banca deve ter no mínimo 2 membros avaliadores além do orientador (RN05).");

        var banca = new Banca
        {
            TccId = idTcc,
            DataHora = dto.DataHora.ToUniversalTime(),
            Local = dto.Local
        };

        _context.Banca.Add(banca);
        await _context.SaveChangesAsync(); // Salva para gerar o Id da Banca

        // 4. Alocar Membros (Avaliadores)
        foreach (var profId in dto.ProfessoresIds)
        {
            _context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = profId });
        }

        foreach (var extId in dto.MembrosExternosIds)
        {
            _context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = extId });
        }

        await _context.SaveChangesAsync();
        return Ok("Banca agendada com sucesso!");
    }

    [HttpGet("aguardando-banca")]
    public async Task<IActionResult> GetTccsAguardandoBanca()
    {
        var lista = await _context.Tccs
            .Include(t => t.Aluno)
            .Include(t => t.Orientador)
            .Where(t => t.Status == StatusTcc.AguardandoDefesa)
            .Select(t => new TccAguardandoBancaDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                NomeAluno = t.Aluno!.Nome,
                NomeOrientador = t.Orientador!.Nome
            })
            .ToListAsync();
        return Ok(lista);
    }

    [HttpGet("bancas-pendentes-resultado")]
    public async Task<IActionResult> GetBancasPendentesResultado()
    {
        var bancas = await _context.Banca
            .Include(b => b.Tcc)
                .ThenInclude(t => t.Aluno)
            .Where(b => b.Tcc!.Status == StatusTcc.AguardandoDefesa && b.NotaFinal == null)
            .Select(b => new BancaPendenteDto {
                TccId = b.Id,
                DataHora =  b.DataHora,
                Local = b.Local,
                TccTitulo = b.Tcc.Titulo,
                NomeAluno = b.Tcc.Aluno!.Nome
            })
            .ToListAsync(); 
        return Ok(bancas);
    }

    [HttpPost("banca/{idBanca}/registrar-resultado")]
    public async Task<IActionResult> RegistrarResultadoBanca(int idBanca, [FromForm] decimal notaFinal, [FromForm] IFormFile arquivoAta)
    {
        var banca = await _context.Banca
            .Include(b => b.Tcc)
            .FirstOrDefaultAsync(b => b.Id == idBanca);

        if (banca == null) return NotFound("Banca não encontrada.");
        if (arquivoAta == null || arquivoAta.Length == 0) return BadRequest("Resultado já registrado para esta banca.");

        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "atas");
        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

        var uniqueFileName = Guid.NewGuid().ToString() + "_" + arquivoAta.FileName;
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await arquivoAta.CopyToAsync(stream);
        }

        banca.NotaFinal = notaFinal;
        banca.AtaCaminho = $"/uploads/atas/{uniqueFileName}";

        banca.Tcc!.Status = StatusTcc.Aprovado;

        await _context.SaveChangesAsync();
        return Ok("Resultado da banca registrado com sucesso!");
    }
}