using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Api.Middleware;
using TccManager.Api.Services;
using TccManager.Api.Services.Notifications;
using TccManager.Api.Services.Pdf;
using TccManager.Api.Services.Storage;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Formatting;
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
    private readonly IAtaPdfService _ataPdfService;
    private readonly IRascunhoAtaTokenService _rascunhoTokenService;
    private const decimal notaMinimaAprovacao = 60.0m;

    // Categoria dedicada de auditoria, mesmo padrão de TccController — issue #76 (D7):
    // a rejeição de proposta decide o encerramento negativo do TCC de um aluno e precisa
    // de trilha estruturada (RNF-01). "TccManager.Api.Auditoria" já tem Override explícito
    // em appsettings.json para não ser descartada pelo MinimumLevel Warning do Serilog.
    private readonly ILogger _auditLogger;

    public CoordenadorController(
        AppDbContext context,
        ISanitizerService sanitizerService,
        ITccNotificationService notificationService,
        IStorageService storageService,
        IAtaPdfService ataPdfService,
        IRascunhoAtaTokenService rascunhoTokenService,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _sanitizerService = sanitizerService;
        _notificationService = notificationService;
        _storageService = storageService;
        _ataPdfService = ataPdfService;
        _rascunhoTokenService = rascunhoTokenService;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
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
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetProfessores([FromQuery] PaginacaoQuery paginacao, CancellationToken cancellationToken)
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
            .ToPagedResultAsync(paginacao, cancellationToken);

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
                Resumo = t.Resumo,
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

        // Auditoria (RNF-01, achado A09-1 da revisão de segurança da issue #76): mesma
        // disciplina de RejeitarProposta — designar orientador é igualmente uma decisão
        // terminal sobre a proposta (aloca carga de um professor, aprova o TCC), então precisa
        // da mesma trilha. Logo após o SaveChanges (achado A09-3): a auditoria não pode
        // depender do envio de notificação (que já é best-effort/try-catch) para existir.
        var coordenadorIdClaimDesignacao = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _auditLogger.LogInformation(
            "Orientador designado pelo Coordenador. TccId: {TccId}, AlunoId: {AlunoId}, OrientadorId: {OrientadorId}, CoordenadorId: {CoordenadorId}",
            tcc.Id,
            tcc.AlunoId,
            tcc.OrientadorId,
            coordenadorIdClaimDesignacao);

        // Aprovação (RF7) — única via desde a issue #76 (rota do Professor removida).
        await _notificationService.NotificarPropostaAprovadaAsync(tcc.Id);

        return Ok("Orientador designado com sucesso.");
    }

    // Issue #76 (D7): rejeição de proposta passa a ser exclusiva do Coordenador — antes era
    // POST api/orientador/propostas/{id}/rejeitar, sem nenhuma verificação de vínculo entre o
    // Professor autenticado e a proposta (achado de RBAC). Espelha DesignarOrientador (mesma
    // guarda de status, mesmo estilo de resposta 200/404).
    [HttpPut("propostas/{id}/rejeitar")]
    public async Task<IActionResult> RejeitarProposta(int id, [FromBody] RejeicaoDto dto)
    {
        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == id && t.Status == StatusTcc.Pendente);
        if (tcc == null) return NotFound("Proposta não encontrada ou já processada.");

        tcc.Status = StatusTcc.Reprovado;
        // Issue #73 (achado A10-1): sanitizar antes de persistir e antes de medir o limite de
        // 2000 caracteres da coluna (RejeicaoDtoValidator já valida isso sobre o valor
        // sanitizado, mas o sanitizador é reaplicado aqui, não reaproveitado do validador).
        tcc.MotivoRejeicao = _sanitizerService.Sanitizar(dto.Motivo);

        await _context.SaveChangesAsync();

        // Auditoria (RNF-01): só ids, nunca o texto do motivo (campo livre preenchido por
        // humano — duplicá-lo no log espalharia conteúdo potencialmente sensível para fora do
        // banco, mesma disciplina de PII do projeto). Logo após o SaveChanges, não depois da
        // notificação (achado A09-3 da revisão de segurança): a trilha de auditoria de uma
        // decisão terminal não pode depender do envio de e-mail ter sido tentado.
        var coordenadorIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        _auditLogger.LogInformation(
            "Proposta rejeitada pelo Coordenador. TccId: {TccId}, AlunoId: {AlunoId}, CoordenadorId: {CoordenadorId}",
            tcc.Id,
            tcc.AlunoId,
            coordenadorIdClaim);

        // Depois do SaveChanges, como em todos os outros pontos do projeto: o serviço lê o
        // estado já persistido (o template do RF8 usa o motivo).
        await _notificationService.NotificarPropostaRejeitadaAsync(tcc.Id);

        return Ok("Proposta rejeitada com sucesso.");
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
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetMembrosExternos([FromQuery] PaginacaoQuery paginacao, CancellationToken cancellationToken)
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
            .ToPagedResultAsync(paginacao, cancellationToken);

        return Ok(membros);
    }

    [HttpPost("membros-externos")]
    public async Task<IActionResult> AdicionarMembroExterno([FromBody] MembroExternoDto dto)
    {
        // Recebe o DTO (sem Id) em vez da entidade inteira: MembroExterno.Id é
        // Identity/auto-gerado, e aceitar um Id não-default vindo do corpo da requisição
        // (mass assignment) arrisca SqlException ao tentar inserir valor explícito numa
        // coluna Identity — mesmo padrão de UsuarioController.CreateUsuario.
        var membro = new MembroExterno
        {
            Nome = _sanitizerService.Sanitizar(dto.Nome)!,
            Email = dto.Email,
            Instituicao = _sanitizerService.Sanitizar(dto.Instituicao)!
        };

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

        membro.Nome = _sanitizerService.Sanitizar(dto.Nome)!;
        membro.Email = dto.Email;
        membro.Instituicao = _sanitizerService.Sanitizar(dto.Instituicao)!;

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
            .Where(t => t.Status == StatusTcc.AguardandoDefesa && !_context.Banca.Any(b => b.TccId == t.Id))
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
            .Include(b => b.Avaliadores)
                .ThenInclude(a => a.MembroExterno)
            .Where(b => b.Tcc!.Status == StatusTcc.AguardandoDefesa && b.NotaFinal == null)
            .Select(b => new BancaPendenteDto
            {
                TccId = b.Id,
                DataHora = b.DataHora,
                Local = b.Local,
                TccTitulo = b.Tcc.Titulo,
                NomeAluno = b.Tcc.Aluno!.Nome,
                // Necessário para o botão de reenvio de token do rascunho (RF-06), um por
                // membro externo — ver docs/arquitetura/2026-07-13-pdf-ata-rascunho-etapa2.md, seção 9.2.
                MembrosExternos = b.Avaliadores
                    .Where(a => a.MembroExternoId != null)
                    .Select(a => new MembroExternoBancaDto
                    {
                        MembroExternoId = a.MembroExternoId!.Value,
                        Nome = a.MembroExterno!.Nome
                    })
                    .ToList()
            })
            .ToListAsync();
        return Ok(bancas);
    }

    [HttpPost("banca/{idBanca}/registrar-resultado")]
    [RequestSizeLimit(UploadLimits.MaxArquivoUploadBytes)]
    public async Task<IActionResult> RegistrarResultadoBanca(
        int idBanca,
        [FromForm] decimal notaFinal,
        [FromForm] IFormFile arquivoAta,
        [FromForm] string? motivoReprovacao)
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

        // Issue #75: [RequestSizeLimit] (no atributo do método) protege a conexão Kestrel
        // real, mas esse corte não é reproduzido pelo TestServer em memória usado pelos
        // testes de integração — ver o mesmo comentário em TccController.EnviarEntrega para
        // o raciocínio completo. Esta é a camada testável.
        if (arquivoAta.Length > UploadLimits.MaxArquivoUploadBytes)
            return BadRequest($"O arquivo excede o tamanho máximo permitido ({UploadLimits.MaxArquivoUploadBytes / (1024 * 1024)} MB).");

        bool aprovado = notaFinal >= notaMinimaAprovacao;

        // Issue #73 (achado A10-1 da revisão de segurança,
        // docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md): motivoReprovacao é
        // parâmetro de form escalar, não passa pelo FluentValidationActionFilter — e não dá
        // pra usar [StringLength] no parâmetro porque isso validaria o valor CRU, não o
        // sanitizado que de fato é persistido (HtmlSanitizer só CODIFICA entidades, nunca
        // decodifica). Sanitiza primeiro, valida (obrigatoriedade E tamanho) sobre o valor JÁ
        // sanitizado — não o cru: um motivo só com tags HTML (ex.: "<b></b>") sanitiza para
        // string vazia, e checar IsNullOrWhiteSpace no valor cru deixaria isso passar como se
        // fosse um motivo válido. Tudo ANTES do upload do arquivo da ata, para não deixar
        // arquivo órfão em storage por causa de um motivo inválido.
        string? motivoSanitizado = null;
        if (!aprovado)
        {
            motivoSanitizado = _sanitizerService.Sanitizar(motivoReprovacao);
            if (string.IsNullOrWhiteSpace(motivoSanitizado))
                // NotaFormatter (não CurrentCulture) — mesmo achado do CI da issue #75 em
                // AtaPdfDocument.cs/TccNotificationService.cs: "60,0" é o formato correto
                // para este público, independente da cultura do SO onde a API roda.
                return BadRequest($"Nota inferior a {NotaFormatter.Formatar(notaMinimaAprovacao)}. É obrigatório informar o motivo da reprovação.");
            if (motivoSanitizado.Length > 2000)
                return BadRequest("O motivo da reprovação deve ter no máximo 2000 caracteres.");
        }

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
            banca.Tcc.MotivoRejeicao = motivoSanitizado;
        }

        await _context.SaveChangesAsync();

        await _notificationService.NotificarResultadoBancaAsync(banca.Id, aprovado);

        var mensagem = aprovado
            ? "Resultado da banca registrado com sucesso! O TCC foi finalizado."
            : "Resultado da banca registrado. O TCC foi reprovado conforme a nota informada.";

        return Ok(mensagem);
    }

    // Issue #72: mesmo achado do GlobalExceptionHandler (issue #71) — o corpo diz "contate o
    // suporte" mas sem correlationId o suporte não teria como localizar a linha de log com o
    // motivo específico da inconsistência (que fica só no servidor, nunca no corpo). Lido de
    // HttpContext.Items (não do header) pelo mesmo motivo documentado em
    // GlobalExceptionHandler: sobrevive mesmo que algo no meio do caminho já tenha limpo a
    // resposta.
    private ObjectResult ErroDadosInconsistentesAtaPdf()
    {
        var correlationId = HttpContext.Items[CorrelationIdMiddleware.ItemsKey] as string;
        return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Não foi possível gerar a ata.",
            Detail = "Dados da banca inconsistentes. Contate o suporte.",
            Extensions = { ["correlationId"] = correlationId }
        });
    }

    [HttpGet("banca/{idBanca}/ata-pdf")]
    [EnableRateLimiting(RateLimitingSetup.GeracaoPdfPolicyName)]
    public async Task<IActionResult> GetAtaPdf(int idBanca)
    {
        var resultado = await _ataPdfService.GerarAtaFinalAsync(idBanca);

        // Sucesso é o único ramo que lê PdfBytes (e é o único caso em que
        // AtaPdfService garante que ele vem preenchido) — default vira 500, não sucesso
        // silencioso, para nunca cair num "!"/NullReferenceException aqui se um novo status
        // for adicionado ao enum e esquecido neste switch (issue #72).
        return resultado.Status switch
        {
            AtaPdfResultadoStatus.Sucesso => File(resultado.PdfBytes!, "application/pdf", $"ata-defesa-{idBanca}.pdf"),
            AtaPdfResultadoStatus.BancaNaoEncontrada => NotFound("Banca não encontrada."),
            AtaPdfResultadoStatus.ResultadoNaoRegistrado => Conflict("O resultado desta banca ainda não foi registrado. Gere a ata após registrar a nota final."),
            AtaPdfResultadoStatus.DadosInconsistentes => ErroDadosInconsistentesAtaPdf(),
            _ => StatusCode(StatusCodes.Status500InternalServerError, "Erro inesperado ao gerar o PDF.")
        };
    }

    [HttpGet("banca/{idBanca}/ata-rascunho-pdf")]
    [EnableRateLimiting(RateLimitingSetup.GeracaoPdfPolicyName)]
    public async Task<IActionResult> GetAtaRascunhoPdf(int idBanca)
    {
        var resultado = await _ataPdfService.GerarAtaRascunhoAsync(idBanca);

        return resultado.Status switch
        {
            AtaPdfResultadoStatus.Sucesso => File(resultado.PdfBytes!, "application/pdf", $"ata-rascunho-{idBanca}.pdf"),
            AtaPdfResultadoStatus.BancaNaoEncontrada => NotFound("Banca não encontrada."),
            AtaPdfResultadoStatus.ResultadoJaRegistrado => StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado. Utilize o PDF final."),
            AtaPdfResultadoStatus.DadosInconsistentes => ErroDadosInconsistentesAtaPdf(),
            _ => StatusCode(StatusCodes.Status500InternalServerError, "Erro inesperado ao gerar o PDF.")
        };
    }

    /// <summary>
    /// RF-06: revoga o token vigente do membro externo para a banca e gera/envia um novo
    /// (caso do e-mail perdido/não entregue — ver docs/requisitos, RF-06).
    /// </summary>
    [HttpPost("banca/{idBanca}/membro-externo/{idMembroExterno}/reenviar-rascunho")]
    public async Task<IActionResult> ReenviarRascunhoAta(int idBanca, int idMembroExterno)
    {
        var vinculo = await _context.BancaAvaliadores
            .Include(ba => ba.Banca)
            .FirstOrDefaultAsync(ba => ba.BancaId == idBanca && ba.MembroExternoId == idMembroExterno);

        if (vinculo?.Banca == null)
            return NotFound("Este membro externo não é avaliador da banca informada.");

        if (vinculo.Banca.NotaFinal != null)
            return StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado; não é possível reenviar o rascunho.");

        var tokenBruto = await _rascunhoTokenService.GerarTokenAsync(idBanca, idMembroExterno);

        await _notificationService.NotificarReenvioRascunhoAsync(idBanca, idMembroExterno, tokenBruto);

        return Ok("Novo link de acesso ao rascunho enviado com sucesso.");
    }

    [HttpGet("bancas-concluidas")]
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetBancasConcluidas([FromQuery] PaginacaoQuery paginacao, CancellationToken cancellationToken)
    {
        var bancas = await _context.Banca
            .Where(b => b.NotaFinal != null)
            .OrderByDescending(b => b.DataHora)
            .Select(b => new BancaConcluidaDto
            {
                BancaId = b.Id,
                TccTitulo = b.Tcc!.Titulo,
                NomeAluno = b.Tcc.Aluno!.Nome,
                DataHora = b.DataHora,
                NotaFinal = b.NotaFinal!.Value,
                // Aprovado deriva do Status já persistido (fonte de verdade histórica da
                // decisão tomada em RegistrarResultadoBanca), não recomputado a partir da
                // nota — ver docs/dados/2026-07-13-pdf-ata-questpdf.md, seção 5.
                Aprovado = b.Tcc.Status == StatusTcc.Finalizado
            })
            .ToPagedResultAsync(paginacao, cancellationToken);

        return Ok(bancas);
    }
}