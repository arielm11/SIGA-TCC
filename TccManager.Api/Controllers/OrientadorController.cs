using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
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

    // Issue #81 (D10): categoria dedicada de auditoria, mesmo padrão de TccController e
    // CoordenadorController — o veredito sobre uma entrega Final decide se o TCC do aluno
    // avança ou volta para correção (RNF-01). "TccManager.Api.Auditoria" já tem Override
    // explícito em appsettings.json para não ser descartada pelo MinimumLevel Warning do
    // Serilog.
    private readonly ILogger _auditLogger;

    public OrientadorController(
        AppDbContext context,
        ISanitizerService sanitizerService,
        ITccNotificationService notificationService,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _sanitizerService = sanitizerService;
        _notificationService = notificationService;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
    }

    [HttpGet("dashboard")]
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetDaboard(CancellationToken cancellationToken)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

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
            }).ToListAsync(cancellationToken);

        var dashboard = new DashboardOrientadorDto
        {
            OrientandosAtivos = ativos
        };

        return Ok(dashboard);
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

    // Issue #81 (D3): veredito explícito do orientador sobre uma entrega — endpoint dedicado,
    // não uma extensão de RegistrarFeedback (naturezas diferentes: RegistrarFeedback é
    // reeditável e cosmético, o veredito sobre uma Final tem efeito de sistema irreversível).
    // Mesmo filtro de vínculo já usado em RegistrarFeedback (e.Tcc!.OrientadorId == profId).
    [HttpPost("entregas/{idEntrega}/aprovar")]
    public async Task<IActionResult> AprovarEntrega(int idEntrega)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var entrega = await _context.Entregas
            .Include(e => e.Tcc)
            .FirstOrDefaultAsync(e => e.Id == idEntrega && e.Tcc!.OrientadorId == profId);

        if (entrega == null) return NotFound("Entrega não encontrada ou você não tem permissão para acessar.");

        // D9: só é possível registrar veredito enquanto o TCC está em acompanhamento —
        // impede alterar o veredito depois do aceite final. RegistrarFeedback não recebe
        // esta guarda (assimetria deliberada: guarda nova só em código novo, RNF-02).
        if (entrega.Tcc!.Status != StatusTcc.Aprovado && entrega.Tcc.Status != StatusTcc.EmAndamento)
            return BadRequest("Só é possível registrar um veredito enquanto o TCC está em acompanhamento pelo orientador.");

        // D8: uma Final já Rejeitada é terminal — "desrejeitá-la" seria um UPDATE
        // reintroduzindo a linha no índice único filtrado (UX_Entregas_TccId_Final), que
        // pode já estar ocupado pela nova Final enviada pelo aluno. Sem catch
        // correspondente, isso viraria 500.
        if (entrega.Tipo == TipoEntrega.Final && entrega.Status == StatusEntrega.Rejeitada)
            return Conflict("Esta entrega final já foi rejeitada e o ciclo foi reaberto. Avalie a nova versão enviada pelo aluno.");

        entrega.Status = StatusEntrega.Aprovada;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Achado A06-1/A10 da revisão de segurança: backstop atômico do índice único
            // filtrado (UX_Entregas_TccId_Final) contra a checagem de aplicação acima (D8),
            // que sozinha não impede a corrida entre duas requisições concorrentes: A lê esta
            // Final como não-rejeitada; B a rejeita e comita, liberando o aluno para reenviar
            // uma nova Final; A comita "Aprovada" por último, reintroduzindo a linha antiga no
            // índice. Sem este catch, isso vira 500. Não logar a exceção crua (mesmo padrão de
            // TccController.EnviarEntrega/UsuarioController).
            _auditLogger.LogWarning(
                "Conflito de concorrência ao aprovar entrega (índice único de Final). EntregaId: {EntregaId}, TccId: {TccId}",
                entrega.Id, entrega.TccId);
            return Conflict("Esta entrega final já foi rejeitada e o ciclo foi reaberto. Avalie a nova versão enviada pelo aluno.");
        }

        // Auditoria (D10): só ids/tipo/veredito, nunca o texto do motivo (não se aplica aqui,
        // mas mantém o mesmo formato do log de RejeitarEntrega).
        _auditLogger.LogInformation(
            "Veredito registrado pelo Professor: Aprovada. EntregaId: {EntregaId}, TccId: {TccId}, AlunoId: {AlunoId}, OrientadorId: {OrientadorId}, TipoEntrega: {TipoEntrega}",
            entrega.Id,
            entrega.TccId,
            entrega.Tcc.AlunoId,
            profId,
            entrega.Tipo);

        await _notificationService.NotificarVeredictoEntregaAsync(entrega.Id, aprovada: true);

        return Ok("Entrega aprovada.");
    }

    [HttpPost("entregas/{idEntrega}/rejeitar")]
    public async Task<IActionResult> RejeitarEntrega(int idEntrega, [FromBody] RejeicaoDto dto)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var entrega = await _context.Entregas
            .Include(e => e.Tcc)
            .FirstOrDefaultAsync(e => e.Id == idEntrega && e.Tcc!.OrientadorId == profId);

        if (entrega == null) return NotFound("Entrega não encontrada ou você não tem permissão para acessar.");

        if (entrega.Tcc!.Status != StatusTcc.Aprovado && entrega.Tcc.Status != StatusTcc.EmAndamento)
            return BadRequest("Só é possível registrar um veredito enquanto o TCC está em acompanhamento pelo orientador.");

        if (entrega.Tipo == TipoEntrega.Final && entrega.Status == StatusEntrega.Rejeitada)
            return Conflict("Esta entrega final já foi rejeitada e o ciclo foi reaberto. Avalie a nova versão enviada pelo aluno.");

        entrega.Status = StatusEntrega.Rejeitada;
        // D4: motivo obrigatório persistido em Entrega.Feedback (sem coluna nova) — mesma
        // disciplina de sanitização de CoordenadorController.RejeitarProposta. Sobrescreve
        // qualquer feedback anterior desta entrega (trade-off aceito, ver documento de
        // arquitetura seção 6).
        entrega.Feedback = _sanitizerService.Sanitizar(dto.Motivo);

        await _context.SaveChangesAsync();

        _auditLogger.LogInformation(
            "Veredito registrado pelo Professor: Rejeitada. EntregaId: {EntregaId}, TccId: {TccId}, AlunoId: {AlunoId}, OrientadorId: {OrientadorId}, TipoEntrega: {TipoEntrega}",
            entrega.Id,
            entrega.TccId,
            entrega.Tcc.AlunoId,
            profId,
            entrega.Tipo);

        await _notificationService.NotificarVeredictoEntregaAsync(entrega.Id, aprovada: false);

        var mensagem = entrega.Tipo == TipoEntrega.Final
            ? "Entrega final rejeitada. O aluno já pode enviar uma nova versão."
            : "Entrega rejeitada.";

        return Ok(mensagem);
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

        // Issue #81 (D7): passa a exigir que a Final mais recente esteja Aprovada, não
        // apenas presente — sem isso o Professor contornaria o próprio veredito de rejeição
        // e a reabertura do ciclo não teria efeito prático nenhum. Três mensagens distintas
        // porque cada situação pede uma ação diferente do professor.
        var temEntregaFinal = tcc.Entregas.Any(e => e.Tipo == TipoEntrega.Final);
        if (!temEntregaFinal)
            return BadRequest("Não é possível dar o aceite final. O aluno ainda não enviou a Versão Final do trabalho (RN03).");

        var temFinalAprovada = tcc.Entregas.Any(e => e.Tipo == TipoEntrega.Final && e.Status == StatusEntrega.Aprovada);
        if (!temFinalAprovada)
        {
            var todasRejeitadas = tcc.Entregas
                .Where(e => e.Tipo == TipoEntrega.Final)
                .All(e => e.Status == StatusEntrega.Rejeitada);

            return BadRequest(todasRejeitadas
                ? "A versão final foi rejeitada. Aguarde o novo envio do aluno."
                : "A versão final ainda não foi avaliada. Aprove a entrega final antes de conceder o aceite.");
        }

        tcc.Status = StatusTcc.AguardandoDefesa;
        await _context.SaveChangesAsync();

        // Auditoria (P-03/D10): DarAceiteFinal não tinha trilha antes desta issue; com o
        // _auditLogger introduzido por D10 no mesmo controller, fechar essa lacuna custa uma
        // linha, e o endpoint passa a ser justamente o que consome o veredito registrado
        // pelos dois endpoints novos. Logo após o SaveChanges, independentemente da
        // notificação (best-effort).
        _auditLogger.LogInformation(
            "Aceite final concedido pelo Professor. TccId: {TccId}, AlunoId: {AlunoId}, OrientadorId: {OrientadorId}",
            tcc.Id,
            tcc.AlunoId,
            profId);

        await _notificationService.NotificarAceiteFinalAsync(tcc.Id);

        return Ok("Aceite final registrado com sucesso. O TCC agora aguarda o agendamento da Banca.");
    }
}
