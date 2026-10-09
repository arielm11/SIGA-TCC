using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TccManager.Api.Configuration;
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

        // Issue #151 (achado C4): Include() ignorado em silêncio pelo EF Core — o Select
        // abaixo já traduz t.Aluno.Nome para SQL sem precisar dele.
        var ativos = await _context.Tccs
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

        // Issue #151 (achado C3): leitura pura (nunca salva) que materializava a entidade
        // completa rastreada sem necessidade.
        var tcc = await _context.Tccs
            .AsNoTracking()
            .Include(t => t.Aluno)
            .Include(t => t.Entregas.OrderByDescending(e => e.DataEnvio))
            .Include(t => t.Acompanhamentos.OrderByDescending(a => a.DataReuniao))
            .FirstOrDefaultAsync(t => t.Id == idTcc && t.OrientadorId == profId);

        if (tcc == null) return NotFound("TCC não encontrado ou você não tem permissão para acessar.");

        // Issue #144 (achado A3/B8): projeta para DTO em vez de devolver a entidade Tcc crua
        // — antes vazava, entre outros, PrecisaTrocarSenha/LimiteOrientandos/Ativo do Aluno e
        // o caminho interno de armazenamento das Entregas (mesma classe de risco do
        // vazamento de SenhaHash corrigido na #137).
        return Ok(TccDetalheDto.DeEntidade(tcc, nomeAluno: tcc.Aluno?.Nome));
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

        // Issue #145 (achado B2): RegistrarFeedback é anterior às guardas de veredito da
        // issue #81 (D9) e nunca recebeu a mesma proteção — sem isso, dava pra editar
        // nota/parecer de uma entrega mesmo com o TCC já Finalizado/Reprovado, sem trilha de
        // auditoria. Mesma guarda de AprovarEntrega/RejeitarEntrega.
        if (entrega.Tcc!.Status != StatusTcc.Aprovado && entrega.Tcc.Status != StatusTcc.EmAndamento)
            return BadRequest("Só é possível registrar feedback enquanto o TCC está em acompanhamento pelo orientador.");

        entrega.Feedback = _sanitizerService.Sanitizar(dto.Feedback);
        entrega.Nota = dto.Nota;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Issue #145 (achado B2): backstop atômico do RowVersion de Entrega contra uma
            // corrida com AprovarEntrega/RejeitarEntrega (ou outro RegistrarFeedback)
            // concorrente sobre a mesma Entrega.
            _auditLogger.LogWarning(
                "Conflito de concorrência ao registrar feedback. EntregaId: {EntregaId}, TccId: {TccId}",
                entrega.Id, entrega.TccId);
            return Conflict("Esta entrega foi alterada por outra ação simultânea. Atualize a página e tente novamente.");
        }

        // Issue #143 (achado M6): altera a nota da entrega e não tinha nenhum registro de
        // auditoria. Nunca o texto do parecer, só ids/nota.
        _auditLogger.LogInformation(
            "Feedback registrado. EntregaId: {EntregaId}, TccId: {TccId}, OrientadorId: {OrientadorId}, Nota: {Nota}",
            entrega.Id, entrega.TccId, profId, entrega.Nota);

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
        catch (DbUpdateConcurrencyException)
        {
            // Issue #145 (achado B2): backstop atômico do RowVersion de Entrega contra uma
            // corrida com RegistrarFeedback/RejeitarEntrega concorrente sobre a mesma Entrega.
            _auditLogger.LogWarning(
                "Conflito de concorrência ao aprovar entrega. EntregaId: {EntregaId}, TccId: {TccId}",
                entrega.Id, entrega.TccId);
            return Conflict("Esta entrega foi alterada por outra ação simultânea. Atualize a página e tente novamente.");
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

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Issue #145 (achado B2): backstop atômico do RowVersion de Entrega contra uma
            // corrida com RegistrarFeedback/AprovarEntrega concorrente sobre a mesma Entrega.
            _auditLogger.LogWarning(
                "Conflito de concorrência ao rejeitar entrega. EntregaId: {EntregaId}, TccId: {TccId}",
                entrega.Id, entrega.TccId);
            return Conflict("Esta entrega foi alterada por outra ação simultânea. Atualize a página e tente novamente.");
        }

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

        // Issue #143 (achado M6): CRUD de acompanhamentos não tinha nenhum registro de
        // auditoria. Só ids, nunca o texto da Ata (nunca logar texto livre).
        _auditLogger.LogInformation(
            "Acompanhamento registrado. TccId: {TccId}, AcompanhamentoId: {AcompanhamentoId}, OrientadorId: {OrientadorId}",
            idTcc, novoAcompanhamento.Id, profId);

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

        _auditLogger.LogInformation(
            "Acompanhamento editado. TccId: {TccId}, AcompanhamentoId: {AcompanhamentoId}, OrientadorId: {OrientadorId}",
            idTcc, idAcompanhamento, profId);

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

        _auditLogger.LogInformation(
            "Acompanhamento excluído. TccId: {TccId}, AcompanhamentoId: {AcompanhamentoId}, OrientadorId: {OrientadorId}",
            idTcc, idAcompanhamento, profId);

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

        // Issue #120: sem esta guarda, DarAceiteFinal aceitava um TCC já Finalizado/Reprovado
        // (a Entrega Final aprovada continua lá) e o devolvia para AguardandoDefesa — rearmando
        // um TCC que RegistrarResultadoBanca já tinha concluído, permitindo sobrescrever a nota
        // final e a ata já registradas. Mesma guarda dupla (Aprovado/EmAndamento) já usada nos
        // outros 8 pontos do sistema que fazem essa checagem (a UI, DetalhesTcc.razor, já
        // assume isso implicitamente).
        if (tcc.Status != StatusTcc.Aprovado && tcc.Status != StatusTcc.EmAndamento)
            return BadRequest("Não é possível dar o aceite final: o TCC não está em andamento de orientação (RN03).");

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

    // ── Issue #112: devolve ao Professor autonomia real, corretamente escopada ao próprio
    // vínculo, para decidir sobre a proposta que o solicita (D1-D5 do documento de arquitetura).
    // Prefixo de rota "propostas-solicitadas" (D2): distinto de "propostas/{id}/..." para não
    // colidir com as rotas removidas por #76 (POST .../propostas/{id}/aprovar|rejeitar, que
    // continuam 404 de roteamento — travado por OrientadorNotificacaoIntegracao_Tests).

    /// <summary>
    /// Issue #112 (D1/RNF04): propostas Pendentes em que o Professor autenticado é o
    /// OrientadorSolicitadoId — vínculo no próprio WHERE da query, nunca checagem posterior.
    /// </summary>
    [HttpGet("propostas-solicitadas")]
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetPropostasSolicitadas([FromQuery] PaginacaoQuery paginacao, CancellationToken cancellationToken)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        // Issue #151 (achado C4): Include() ignorado em silêncio pelo EF Core — o Select
        // abaixo já traduz t.Aluno.Nome para SQL sem precisar dele.
        var propostas = await _context.Tccs
            .Where(t => t.Status == StatusTcc.Pendente && t.OrientadorSolicitadoId == profId)
            .OrderBy(t => t.DataCriacao)
            .Select(t => new TccResumoDto
            {
                Id = t.Id,
                Titulo = t.Titulo,
                Resumo = t.Resumo,
                NomeAluno = t.Aluno != null ? t.Aluno.Nome : "Desconhecido",
                DataCriacao = t.DataCriacao,
                Status = t.Status
            })
            .ToPagedResultAsync(paginacao, cancellationToken);

        return Ok(propostas);
    }

    /// <summary>
    /// Issue #112 (D1/D5): aprovação pelo Professor solicitado — mesmo efeito de
    /// CoordenadorController.DesignarOrientador (OrientadorId = profId, Status = Aprovado),
    /// mas sem receber id de professor por corpo/URL (o vínculo já é o próprio profId
    /// autenticado). Resposta uniforme (404) para inexistente/já processada/sem vínculo —
    /// nunca 403, mesmo racional de TccController.DownloadEntrega (D1 da arquitetura).
    /// </summary>
    [HttpPut("propostas-solicitadas/{id}/aprovar")]
    public async Task<IActionResult> AprovarPropostaSolicitada(int id)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t =>
            t.Id == id && t.Status == StatusTcc.Pendente && t.OrientadorSolicitadoId == profId);

        if (tcc == null)
            return NotFound("Proposta não encontrada, já processada ou você não tem permissão para acessá-la.");

        tcc.OrientadorId = profId;
        tcc.Status = StatusTcc.Aprovado;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // D5: mesma proteção de DesignarOrientador — outro ator (Coordenador ou o próprio
            // professor em outra aba) já decidiu sobre esta proposta entre o read e este write.
            _auditLogger.LogWarning(
                "Aprovação de proposta solicitada recusada por conflito de concorrência: a proposta já foi decidida por outra requisição. TccId: {TccId}",
                id);
            return Conflict("Esta proposta já foi decidida por outra ação simultânea. Atualize a lista e tente novamente.");
        }

        // Auditoria (D10/RF07): logo após o SaveChanges, antes da notificação (best-effort).
        _auditLogger.LogInformation(
            "Proposta aprovada pelo Professor solicitado. TccId: {TccId}, AlunoId: {AlunoId}, OrientadorId: {OrientadorId}",
            tcc.Id,
            tcc.AlunoId,
            tcc.OrientadorId);

        // Reusa a mesma notificação de DesignarOrientador (RF7) — o aluno não distingue quem
        // decidiu, só que a proposta foi aprovada.
        await _notificationService.NotificarPropostaAprovadaAsync(tcc.Id);

        return Ok("Proposta aprovada com sucesso.");
    }

    /// <summary>
    /// Issue #112 (D1/D5/D9): rejeição pelo Professor solicitado — mesmo efeito de
    /// CoordenadorController.RejeitarProposta (Status = Reprovado, MotivoRejeicao sanitizado).
    /// D9: o sistema não guarda quem rejeitou (Professor ou Coordenador), exceto no log de
    /// auditoria — texto neutro no Client (MeuTcc.razor), não escopo desta API.
    /// </summary>
    [HttpPut("propostas-solicitadas/{id}/rejeitar")]
    public async Task<IActionResult> RejeitarPropostaSolicitada(int id, [FromBody] RejeicaoDto dto)
    {
        var profIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(profIdClaim) || !int.TryParse(profIdClaim, out int profId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t =>
            t.Id == id && t.Status == StatusTcc.Pendente && t.OrientadorSolicitadoId == profId);

        if (tcc == null)
            return NotFound("Proposta não encontrada, já processada ou você não tem permissão para acessá-la.");

        tcc.Status = StatusTcc.Reprovado;
        tcc.MotivoRejeicao = _sanitizerService.Sanitizar(dto.Motivo);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _auditLogger.LogWarning(
                "Rejeição de proposta solicitada recusada por conflito de concorrência: a proposta já foi decidida por outra requisição. TccId: {TccId}",
                id);
            return Conflict("Esta proposta já foi decidida por outra ação simultânea. Atualize a lista e tente novamente.");
        }

        // Auditoria (D10/RF07): só ids, nunca o texto do motivo — mesma disciplina de
        // CoordenadorController.RejeitarProposta.
        _auditLogger.LogInformation(
            "Proposta rejeitada pelo Professor solicitado. TccId: {TccId}, AlunoId: {AlunoId}, ProfessorId: {ProfessorId}",
            tcc.Id,
            tcc.AlunoId,
            profId);

        await _notificationService.NotificarPropostaRejeitadaAsync(tcc.Id);

        return Ok("Proposta rejeitada com sucesso.");
    }
}
