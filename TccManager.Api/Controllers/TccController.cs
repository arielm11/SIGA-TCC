using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Api.Services;
using TccManager.Api.Services.Storage;
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
    private readonly ISanitizerService _sanitizerService;
    private readonly IStorageService _storageService;
    private readonly ILogger<TccController> _logger;

    // Categoria dedicada de auditoria (upload/compensação/download de entrega): o
    // MinimumLevel padrão do Serilog é Warning, que descartaria eventos de auditoria em
    // Information. "TccManager.Api.Auditoria" tem Override explícito em appsettings.json
    // para não perder essa trilha — mesmo padrão de "TccManager.Api.RateLimiting" em
    // RateLimitingSetup. Ver docs/seguranca/2026-08-18-fix-upload-storage-hardening.md, A09-1.
    private readonly ILogger _auditLogger;

    public TccController(
        AppDbContext context,
        ISanitizerService sanitizerService,
        IStorageService storageService,
        ILogger<TccController> logger,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _sanitizerService = sanitizerService;
        _storageService = storageService;
        _logger = logger;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
    }

    [HttpGet("meu-tcc")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> GetMeuTcc()
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized("Sessão inválida.");

        var tcc = await _context.Tccs
            .Include(t => t.Entregas)
            .Where(t => t.AlunoId == alunoId)
            .OrderByDescending(t => t.DataCriacao)
            .FirstOrDefaultAsync();

        if (tcc == null)
            return NoContent();

        return Ok(tcc);
    }

    [HttpPost("proposta")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> SubmeterProposta([FromBody] PropostaTccDto dto)
    {
        var alunoClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (alunoClaim == null || !int.TryParse(alunoClaim.Value, out int alunoId))
            return Unauthorized("Usuário não identificado.");

        var existeTccAtivo = await _context.Tccs.AnyAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);
        if (existeTccAtivo)
            return BadRequest("Você já possui um TCC ativo. Não é possível submeter outra proposta.");

        var tcc = new Tcc
        {
            Titulo = _sanitizerService.Sanitizar(dto.Titulo)!,
            Resumo = _sanitizerService.Sanitizar(dto.Resumo)!,
            AlunoId = alunoId,
            Status = StatusTcc.Pendente,
            DataCriacao = DateTime.UtcNow
        };

        _context.Tccs.Add(tcc);
        await _context.SaveChangesAsync();

        return Ok(tcc);
    }

    [HttpDelete("proposta/{id}")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> ExcluirProposta(int id)
    {
        var alunoClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (alunoClaim == null || !int.TryParse(alunoClaim.Value, out int alunoId))
            return Unauthorized("Usuário não identificado.");

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.Id == id && t.AlunoId == alunoId);

        if (tcc == null) return NotFound("Proposta não encontrada.");

        if (tcc.Status != StatusTcc.Pendente)
            return BadRequest("Apenas propostas pendentes podem ser excluídas.");

        _context.Tccs.Remove(tcc);
        await _context.SaveChangesAsync();

        return Ok("Proposta excluída com sucesso.");
    }

    [HttpGet("entregas")]
    [Authorize(Roles = "Aluno")]
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetMinhasEntregas([FromQuery] PaginacaoQuery paginacao, CancellationToken cancellationToken)
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado, cancellationToken);
        if (tcc == null) return NotFound("TCC não encontrado.");

        var entregas = await _context.Entregas
            .Where(e => e.TccId == tcc.Id)
            .OrderByDescending(e => e.DataEnvio)
            .ToPagedResultAsync(paginacao, cancellationToken);

        return Ok(entregas);
    }

    [HttpPost("entregas")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> EnviarEntrega(
        [FromForm] string tituloEntrega,
        [FromForm] TipoEntrega tipo,
        IFormFile arquivo)
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);

        if (tcc == null || tcc.Status != StatusTcc.Aprovado)
            return BadRequest("Seu TCC precisa estar aprovado para enviar entregas.");

        var jaEnviouFinal = await _context.Entregas.AnyAsync(e => e.TccId == tcc.Id && e.Tipo == TipoEntrega.Final);
        if (jaEnviouFinal)
            return BadRequest("A versão FINAL já foi enviada. O ciclo de entregas está encerrado.");

        // Issue #73 (achado A10-1 da revisão de segurança,
        // docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md): tituloEntrega é
        // parâmetro de form escalar, não passa pelo FluentValidationActionFilter — e não dá
        // pra usar [StringLength] no parâmetro porque isso validaria o valor CRU, não o
        // sanitizado que de fato é persistido (HtmlSanitizer só CODIFICA entidades, nunca
        // decodifica — um título de 200 caracteres crus pode virar mais de 200 depois de
        // sanitizado, e estourar a coluna nvarchar(200) no INSERT). Sanitiza primeiro, mede
        // depois, ANTES do upload do arquivo (para não deixar arquivo órfão em storage por
        // causa de um título grande demais).
        var tituloSanitizado = _sanitizerService.Sanitizar(tituloEntrega) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tituloSanitizado))
            return BadRequest("O título da entrega é obrigatório.");
        if (tituloSanitizado.Length > 200)
            return BadRequest("O título da entrega deve ter no máximo 200 caracteres.");

        if (arquivo == null || arquivo.Length == 0)
            return BadRequest("Nenhum arquivo enviado.");

        var extensoesPermitidas = new[] { ".pdf", ".doc", ".docx", ".zip" };
        var extensao = Path.GetExtension(arquivo.FileName).ToLowerInvariant();

        if (!extensoesPermitidas.Contains(extensao))
            return BadRequest("Formato de arquivo não permitido. Envie apenas PDF, DOC, DOCX ou ZIP.");

        if (tipo == TipoEntrega.Final && tcc.OrientadorId == null)
            return BadRequest("Você não pode enviar a versão FINAL sem ter um Orientador definido (RN03).");

        string caminho;
        using (var streamOriginal = arquivo.OpenReadStream())
        {
            var (assinaturaValida, streamParaUpload) = await ValidarAssinaturaArquivoAsync(
                streamOriginal, extensao, HttpContext.RequestAborted);

            try
            {
                if (!assinaturaValida)
                    return BadRequest("Conteúdo do arquivo não corresponde à extensão informada.");

                caminho = await _storageService.UploadAsync(streamParaUpload, arquivo.FileName, CategoriaArquivo.Entregas);
            }
            finally
            {
                if (!ReferenceEquals(streamParaUpload, streamOriginal))
                    await streamParaUpload.DisposeAsync();
            }
        }

        var entrega = new Entrega
        {
            TccId = tcc.Id,
            Titulo = tituloSanitizado,
            ArquivoCaminho = caminho,
            Tipo = tipo,
            DataEnvio = DateTime.UtcNow
        };

        _context.Entregas.Add(entrega);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Backstop atômico do índice único filtrado (UX_Entregas_TccId_Final) contra o
            // AnyAsync de aplicação acima, que sozinho não impede duas requisições
            // concorrentes de passarem no pre-check e gerarem duas entregas FINAL. Não
            // logar a exceção crua (mesmo padrão de UsuarioController).
            _auditLogger.LogWarning(
                "Falha ao salvar entrega em POST /api/tcc/entregas: possível violação da restrição de unicidade de entrega FINAL. TccId: {TccId}",
                tcc.Id);
            await CompensarUploadOrfaoAsync(caminho, tcc.Id, "violação de unicidade de entrega FINAL");
            return Conflict("A versão FINAL já foi enviada. O ciclo de entregas está encerrado.");
        }
        catch (Exception)
        {
            // Compensação manual: o arquivo já foi gravado em disco antes do SaveChanges
            // (não há transação distribuída entre disco e banco) — sem isso, uma falha
            // aqui deixaria o arquivo órfão em wwwroot/uploads/entregas.
            await CompensarUploadOrfaoAsync(caminho, tcc.Id, "falha ao salvar entrega no banco de dados");
            throw;
        }

        _auditLogger.LogInformation(
            "Entrega enviada com sucesso. TccId: {TccId}, EntregaId: {EntregaId}, Tipo: {Tipo}",
            tcc.Id,
            entrega.Id,
            entrega.Tipo);

        return Ok(entrega);
    }

    /// <summary>
    /// RF-05/hardening: valida a assinatura binária (magic bytes) do arquivo enviado contra
    /// a extensão declarada, para não confiar apenas no nome do arquivo. PDF exige o prefixo
    /// "%PDF-"; DOC (legado) exige a assinatura OLE Compound File; DOCX e ZIP compartilham a
    /// mesma assinatura de contêiner ZIP (DOCX é um pacote OOXML) — não é objetivo desta
    /// validação diferenciar DOCX de ZIP genérico, apenas rejeitar arquivos que não são
    /// nenhum dos tipos esperados.
    /// </summary>
    private static async Task<(bool AssinaturaValida, Stream StreamParaUpload)> ValidarAssinaturaArquivoAsync(
        Stream streamOriginal, string extensao, CancellationToken cancellationToken)
    {
        var streamSeekavel = streamOriginal;

        // arquivo.OpenReadStream() de um IFormFile é seekable no caso normal (buffer em
        // memória/disco pelo model binding); fallback defensivo para o caso (não esperado
        // em produção) de um stream forward-only.
        if (!streamOriginal.CanSeek)
        {
            var buffer = new MemoryStream();
            await streamOriginal.CopyToAsync(buffer, cancellationToken);
            buffer.Seek(0, SeekOrigin.Begin);
            streamSeekavel = buffer;
        }

        var assinaturasEsperadas = ObterAssinaturasEsperadas(extensao);
        var tamanhoCabecalho = assinaturasEsperadas.Length == 0 ? 0 : assinaturasEsperadas.Max(a => a.Length);
        var cabecalho = new byte[tamanhoCabecalho];
        var totalLido = tamanhoCabecalho == 0
            ? 0
            : await streamSeekavel.ReadAsync(cabecalho.AsMemory(0, tamanhoCabecalho), cancellationToken);

        streamSeekavel.Seek(0, SeekOrigin.Begin);

        var assinaturaValida = assinaturasEsperadas.Any(assinatura =>
            totalLido >= assinatura.Length && cabecalho.AsSpan(0, assinatura.Length).SequenceEqual(assinatura));

        return (assinaturaValida, streamSeekavel);
    }

    private static byte[][] ObterAssinaturasEsperadas(string extensao) => extensao switch
    {
        ".pdf" => new[] { new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D } }, // "%PDF-"
        ".docx" => new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } }, // ZIP/OOXML
        ".zip" => new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } }, // ZIP
        ".doc" => new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } }, // OLE Compound File
        _ => Array.Empty<byte[]>()
    };

    // Remove o arquivo já gravado em disco quando o SaveChangesAsync que persistiria a
    // Entrega falha por qualquer motivo — best-effort, nunca propaga (não pode mascarar o
    // erro original que causou a chamada). Log de auditoria sem PII: apenas ids/metadados,
    // nunca o nome original do arquivo.
    private async Task CompensarUploadOrfaoAsync(string caminho, int tccId, string motivo)
    {
        try
        {
            await _storageService.DeleteAsync(caminho);
            _auditLogger.LogInformation(
                "Arquivo de entrega removido por compensação após falha ao salvar no banco. TccId: {TccId}, Motivo: {Motivo}",
                tccId,
                motivo);
        }
        catch (Exception)
        {
            _auditLogger.LogWarning(
                "Falha ao remover arquivo órfão em compensação de upload de entrega. TccId: {TccId}, Motivo: {Motivo}",
                tccId,
                motivo);
        }
    }

    /// <summary>
    /// Download autenticado do arquivo da entrega (substitui o acesso direto e sem
    /// autorização via UseStaticFiles/wwwroot). Autorizado para: o Aluno dono do TCC, o
    /// Orientador do TCC, um Coordenador, ou um Professor avaliador da banca do TCC.
    /// </summary>
    [HttpGet("entregas/{idEntrega}/download")]
    public async Task<IActionResult> DownloadEntrega(int idEntrega)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int usuarioId))
            return Unauthorized();

        var entrega = await _context.Entregas
            .Include(e => e.Tcc)
            .FirstOrDefaultAsync(e => e.Id == idEntrega);

        // Resposta uniforme (404, nunca 403) para entrega inexistente E para entrega sem
        // autorização: evita que um usuário autenticado distinga "id não existe" de "id
        // existe mas não é seu" e enumere o espaço de ids de Entregas.
        if (entrega?.Tcc == null)
            return NotFound("Entrega não encontrada.");

        var tcc = entrega.Tcc;

        var autorizado = tcc.AlunoId == usuarioId
            || tcc.OrientadorId == usuarioId
            || User.IsInRole("Coordenador")
            || await _context.BancaAvaliadores.AnyAsync(ba => ba.ProfessorId == usuarioId && ba.Banca!.TccId == tcc.Id);

        if (!autorizado)
        {
            _auditLogger.LogWarning(
                "Acesso negado em GET /api/tcc/entregas/{{idEntrega}}/download. Solicitante: {SolicitanteId}, EntregaId: {EntregaId}, TccId: {TccId}",
                usuarioId,
                idEntrega,
                tcc.Id);
            return NotFound("Entrega não encontrada.");
        }

        Stream? stream;
        try
        {
            stream = await _storageService.AbrirLeituraAsync(entrega.ArquivoCaminho, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Caminho persistido inconsistente (fora do diretório de uploads) ou arquivo
            // bloqueado/sem permissão de leitura pelo SO — não deve virar 500. Não logar o
            // caminho do arquivo, só ids.
            _auditLogger.LogWarning(
                "Falha ao abrir arquivo para download em GET /api/tcc/entregas/{{idEntrega}}/download. EntregaId: {EntregaId}",
                entrega.Id);
            return NotFound("Arquivo não encontrado.");
        }

        if (stream == null)
            return NotFound("Arquivo não encontrado.");

        _auditLogger.LogInformation(
            "Download de entrega concedido. Solicitante: {SolicitanteId}, EntregaId: {EntregaId}, TccId: {TccId}",
            usuarioId,
            entrega.Id,
            tcc.Id);

        var nomeArquivo = $"entrega-{entrega.Id}{Path.GetExtension(entrega.ArquivoCaminho)}";
        return File(stream, "application/octet-stream", nomeArquivo);
    }

    [HttpGet("entregas/{idEntrega}/feedbacks")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> GetFeedbackEntrega(int idEntrega) 
    { 
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized();

        var entrega = await _context.Entregas
            .Include(e => e.Tcc)
            .FirstOrDefaultAsync(e => e.Id == idEntrega && e.Tcc!.AlunoId == alunoId);

        if (entrega == null)
            return NotFound("Entrega não encontrada.");

        var feedbackResult = new
        { 
            Nota = entrega.Nota,
            Feedback = entrega.Feedback,
            DataEnvio = entrega.DataEnvio
        };

        return Ok(feedbackResult);
    }

    [HttpGet("acompanhamentos")]
    [Authorize(Roles ="Aluno")]
    public async Task<IActionResult> GetAcompanhentos() 
    {
        var alunoClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoClaim) || !int.TryParse(alunoClaim, out int alunoId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);
        if (tcc == null)
            return NotFound("TCC não encontrado.");

        var acompanhamentos = await _context.Acompanhamentos
            .Where(a => a.TccId == tcc.Id)
            .OrderByDescending(a => a.DataReuniao)
            .ToListAsync();

        return Ok(acompanhamentos);
    }

    [HttpGet("minha-banca")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> GetMinhaBanca() 
    { 
        var alunoClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoClaim) || !int.TryParse(alunoClaim, out int alunoId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);
        if (tcc == null)
            return NotFound("TCC não encontrado.");

        var banca = await _context.Banca
            .Include(b => b.Avaliadores)
                .ThenInclude(a => a.Professor)
            .FirstOrDefaultAsync(b => b.TccId == tcc.Id);

        if (banca == null)
            return NoContent();

        var resultado = new
        {
            DataHora = banca.DataHora,
            Local = banca.Local,
            Professores = banca.Avaliadores.Select(a => a.Professor?.Nome).ToList()
        };

        return Ok(resultado);
    }
}