using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Api.Services;
using TccManager.Api.Services.Notifications;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Professor")]
public class OrientadorController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ISanitizerService _sanitizerService;
    private readonly ITccNotificationService _notificationService;

    public OrientadorController(AppDbContext context, ISanitizerService sanitizerService, ITccNotificationService notificationService)
    {
        _context = context;
        _sanitizerService = sanitizerService;
        _notificationService = notificationService;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDaboard([FromQuery] PaginacaoQuery paginacao)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var pendentes = await _context.Tccs
            .Include(t => t.Aluno)
            .Where(t => t.Status == StatusTcc.Pendente)
            .OrderByDescending(t => t.DataCriacao)
            .Select(t => new TccResumoDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                Resumo = t.Resumo,
                NomeAluno = t.Aluno != null ? t.Aluno.Nome : "Desconecido",
                DataCriacao = t.DataCriacao,
                Status = t.Status
            }).ToPagedResultAsync(paginacao);

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

        await _notificationService.NotificarPropostaAprovadaAsync(tcc.Id);

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
        tcc.MotivoRejeicao = _sanitizerService.Sanitizar(dto.Motivo);

        await _context.SaveChangesAsync();

        await _notificationService.NotificarPropostaRejeitadaAsync(tcc.Id);

        return Ok("Proposta rejeitada.");
    }

    [HttpGet("tcc/{idTcc}")]
    public async Task<IActionResult> GetDetalhesTcc(int idTcc)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs
            .Include(t => t.Aluno)
            .Include(t => t.Entregas.OrderByDescending(e => e.DataEnvio))
            .Include(t => t.Acompanhamentos.OrderByDescending(a => a.DataReuniao))
            .FirstOrDefaultAsync(t => t.Id == idTcc && t.OrientadorId == profId);

        if (tcc == null) return NotFound("TCC não encontrado ou você não tem permissão para acessar.");

        return Ok(tcc);
    }

    [HttpPost("entregas/{IdEntrega}/feedback")]
    public async Task<IActionResult> RegistrarFeedback(int IdEntrega, [FromBody] FeedbackDto dto)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var entrega = await _context.Entregas
            .Include(e => e.Tcc)
            .FirstOrDefaultAsync(e => e.Id == IdEntrega && e.Tcc!.OrientadorId == profId);

        if (entrega == null) return NotFound("Entrega não encontrada ou você não tem permissão para acessar.");

        entrega.Feedback = _sanitizerService.Sanitizar(dto.Feedback);
        entrega.Nota = dto.Nota;

        await _context.SaveChangesAsync();

        await _notificationService.NotificarFeedbackRegistradoAsync(entrega.Id);

        return Ok("Feedback registrado com sucesso.");
    }

    [HttpPost("tcc/{idTcc}/acompanhamentos")]
    public async Task<IActionResult> RegistrarAcompanhamento(int idTcc, [FromBody] AcompanhamentoDto dto)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == idTcc && t.OrientadorId == profId);
        if (tcc == null) return NotFound("TCC não encontrado ou sem permissão.");

        var novoAcompanhamento = new Acompanhamento
        {
            TccId = idTcc,
            DataReuniao = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dto.DataReuniao),
            Ata = _sanitizerService.Sanitizar(dto.Ata)!
        };

        _context.Acompanhamentos.Add(novoAcompanhamento);
        await _context.SaveChangesAsync();

        return Ok("Acompanhamento registrado com sucesso.");
    }

    [HttpPut("tcc/{idTcc}/acompanhamentos/{idAcompanhamento}")]
    public async Task<IActionResult> EditarAcompanhamento(int idTcc, int idAcompanhamento, [FromBody] AcompanhamentoDto dto)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var acompanhamento = await _context.Acompanhamentos
            .Include(a => a.Tcc)
            .FirstOrDefaultAsync(a => a.Id == idAcompanhamento && a.Tcc!.OrientadorId == profId);

        if (acompanhamento == null) return NotFound("Acompanhamento não encontrado ou sem permissão.");

        acompanhamento.DataReuniao = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dto.DataReuniao);
        acompanhamento.Ata = _sanitizerService.Sanitizar(dto.Ata)!;

        await _context.SaveChangesAsync();
        return Ok("Acompanhamento atualizado com sucesso.");
    }

    [HttpDelete("tcc/{idTcc}/acompanhamentos/{idAcompanhamento}")]
    public async Task<IActionResult> DeletarAcompanhamento(int idTcc, int idAcompanhamento)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var acompanhamento = await _context.Acompanhamentos
            .Include(a => a.Tcc)
            .FirstOrDefaultAsync(a => a.Id == idAcompanhamento && a.Tcc!.OrientadorId == profId);

        if (acompanhamento == null) return NotFound("Acompanhamento não encontrado ou sem permissão.");

        _context.Acompanhamentos.Remove(acompanhamento);
        await _context.SaveChangesAsync();
        return Ok("Acompanhamento deletado com sucesso.");
    }

    [HttpPost("tcc/{idTcc}/aceite-final")]
    public async Task<IActionResult> DarAceiteFinal(int idTcc)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs
            .Include(t => t.Entregas)
            .FirstOrDefaultAsync(t => t.Id == idTcc && t.OrientadorId == profId);

        if (tcc == null) return NotFound("TCC não encontrado ou sem permissão.");

        var temEntregaFinal = tcc.Entregas.Any(e => e.Tipo == TipoEntrega.Final);
        if (!temEntregaFinal)
            return BadRequest("Não é possível dar o aceite final. O aluno ainda não enviou a Versão Final do trabalho (RN03).");

        tcc.Status = StatusTcc.AguardandoDefesa;
        await _context.SaveChangesAsync();

        await _notificationService.NotificarAceiteFinalAsync(tcc.Id);

        return Ok("Aceite final registrado com sucesso. O TCC agora aguarda o agendamento da Banca.");
    }
}
