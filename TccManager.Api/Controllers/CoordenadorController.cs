using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Api.Services;
using TccManager.Api.Services.Notifications;
using TccManager.Api.Services.Storage;
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
    private readonly ISanitizerService _sanitizerService;
    private readonly ITccNotificationService _notificationService;
    private readonly IStorageService _storageService;
    private const decimal notaMinimaAprovacao = 60.0m;

    public CoordenadorController(AppDbContext context, ISanitizerService sanitizerService, ITccNotificationService notificationService, IStorageService storageService)
    {
        _context = context;
        _sanitizerService = sanitizerService;
        _notificationService = notificationService;
        _storageService = storageService;
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
    public async Task<IActionResult> GetProfessores([FromQuery] PaginacaoQuery paginacao)
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
            .ToPagedResultAsync(paginacao);

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

        // Mesmo evento/template de OrientadorController.AprovarProposta (RF7).
        await _notificationService.NotificarPropostaAprovadaAsync(tcc.Id);

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
    public async Task<IActionResult> GetMembrosExternos([FromQuery] PaginacaoQuery paginacao)
    {
        var membros = await _context.MembrosExternos
            .OrderBy(m => m.Nome)
            .Select(m => new MembroExternoDto
            {
                Id = m.Id,
                Nome = m.Nome,
                Email = m.Email,
                Instituicao = m.Instituicao
            })
            .ToPagedResultAsync(paginacao);

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

    [HttpPost("tcc/{idTcc}/banca")]
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
            DataHora = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dto.DataHora),
            Local = dto.Local
        };

        _context.Banca.Add(banca);
        await _context.SaveChangesAsync(); // Salva para gerar o Id da Banca

        foreach (var profId in dto.ProfessoresIds)
        {
            _context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = profId });
        }

        foreach (var extId in dto.MembrosExternosIds)
        {
            _context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = extId });
        }

        await _context.SaveChangesAsync();

        // Disparo após o SaveChanges que persiste os BancaAvaliador, para que a lista
        // de avaliadores já esteja completa na resolução de destinatários (RF9).
        await _notificationService.NotificarBancaAgendadaAsync(banca.Id);

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
            .Select(b => new BancaPendenteDto
            {
                TccId = b.Id,
                DataHora = b.DataHora,
                Local = b.Local,
                TccTitulo = b.Tcc.Titulo,
                NomeAluno = b.Tcc.Aluno!.Nome
            })
            .ToListAsync();
        return Ok(bancas);
    }

    [HttpPost("banca/{idBanca}/registrar-resultado")]
    public async Task<IActionResult> RegistrarResultadoBanca(int idBanca, [FromForm] decimal notaFinal, [FromForm] IFormFile arquivoAta, [FromForm] string? motivoReprovacao)
    {
        var banca = await _context.Banca
            .Include(b => b.Tcc)
            .FirstOrDefaultAsync(b => b.Id == idBanca);

        if (banca == null)
            return NotFound("Banca não encontrada.");

        if (banca.Tcc!.Status != StatusTcc.AguardandoDefesa)
            return BadRequest("O resultado desta banca já foi registrado anteriormente. Não é possível registrar novamente.");

        if (arquivoAta == null || arquivoAta.Length == 0)
            return BadRequest("O arquivo da ata é obrigatório para registrar o resultado.");

        bool aprovado = notaFinal >= notaMinimaAprovacao;

        if (!aprovado && string.IsNullOrWhiteSpace(motivoReprovacao))
            return BadRequest($"Nota inferior a {notaMinimaAprovacao:0.0}. É obrigatório informar o motivo da reprovação.");

        string caminho;
        using (var stream = arquivoAta.OpenReadStream())
        {
            caminho = await _storageService.UploadAsync(stream, arquivoAta.FileName, CategoriaArquivo.Atas);
        }

        banca.NotaFinal = notaFinal;
        banca.AtaCaminho = caminho;

        if (aprovado)
        {
            banca.Tcc.Status = StatusTcc.Finalizado;
            banca.Tcc.MotivoRejeicao = null; // limpa qualquer motivo anterior, se houver
        }
        else
        {
            banca.Tcc.Status = StatusTcc.Reprovado;
            banca.Tcc.MotivoRejeicao = _sanitizerService.Sanitizar(motivoReprovacao);
        }

        await _context.SaveChangesAsync();

        await _notificationService.NotificarResultadoBancaAsync(banca.Id, aprovado);

        var mensagem = aprovado
            ? "Resultado da banca registrado com sucesso! O TCC foi finalizado."
            : "Resultado da banca registrado. O TCC foi reprovado conforme a nota informada.";

        return Ok(mensagem);
    }
}