using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Professor")]
public class AvaliadorController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAtaPdfService _ataPdfService;

    // Issue #143 (achado M6/D4): este controller não tinha nenhum ILogger — nem acesso
    // negado nem concedido ao rascunho da ata (documento sensível antes do resultado da
    // banca ser público) era registrado, inconsistente com
    // CoordenadorController.GetAtaAssinada. Mesma categoria dedicada dos demais
    // controllers.
    private readonly ILogger _auditLogger;

    public AvaliadorController(AppDbContext context, IAtaPdfService ataPdfService, ILoggerFactory loggerFactory)
    {
        _context = context;
        _ataPdfService = ataPdfService;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
    }

    [HttpGet("meus-convites")]
    public async Task<IActionResult> GetMeusConvites()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int profId))
            return Unauthorized();

        // A extensão real (.pdf/.doc/.docx/.zip) não é calculável em SQL — Path.GetExtension
        // roda em memória sobre o caminho já materializado, então a projeção fica em duas
        // etapas: busca o caminho da Entrega Final (SQL) e só depois deriva a extensão (C#).
        // Issue #151 (achado C4): os 4 Include()/ThenInclude() abaixo eram ignorados em
        // silêncio pelo EF Core (warning NavigationBaseIncludeIgnored a cada chamada) — o
        // Select já traduz os mesmos caminhos de navegação direto para SQL, sem precisar de
        // Include nenhum.
        var convitesBrutos = await _context.BancaAvaliadores
            .Where(ba => ba.ProfessorId == profId && ba.Banca!.Tcc!.Status != StatusTcc.Finalizado)
            .Select(ba => new
            {
                ba.BancaId,
                DataHora = ba.Banca!.DataHora,
                Local = ba.Banca.Local,
                TccTitulo = ba.Banca.Tcc!.Titulo,
                NomeAluno = ba.Banca.Tcc.Aluno!.Nome,
                NomeOrientador = ba.Banca.Tcc.Orientador!.Nome,
                ArquivoFinal = ba.Banca.Tcc.Entregas
                    .Where(e => e.Tipo == TipoEntrega.Final)
                    .Select(e => new { e.Id, e.ArquivoCaminho })
                    .FirstOrDefault()
            })
            .OrderBy(c => c.DataHora)
            .ToListAsync();

        var convites = convitesBrutos.Select(c => new ConviteBancaDto
        {
            BancaId = c.BancaId,
            DataHora = c.DataHora,
            Local = c.Local,
            TccTitulo = c.TccTitulo,
            NomeAluno = c.NomeAluno,
            NomeOrientador = c.NomeOrientador,
            ArquivoFinalEntregaId = c.ArquivoFinal?.Id,
            ArquivoFinalExtensao = c.ArquivoFinal != null ? Path.GetExtension(c.ArquivoFinal.ArquivoCaminho) : null
        }).ToList();

        return Ok(convites);
    }

    /// <summary>
    /// RF-03/RNF-01 (Etapa 2): download do PDF rascunho para o avaliador interno.
    /// Valida explicitamente o vínculo BancaAvaliador.ProfessorId == usuário autenticado
    /// para a idBanca pedida — sem essa checagem, qualquer professor (inclusive o
    /// orientador, que não deve ter acesso — decisão 6) conseguiria baixar o rascunho de
    /// qualquer banca apenas trocando o idBanca na URL.
    /// </summary>
    [HttpGet("banca/{idBanca}/ata-rascunho-pdf")]
    [EnableRateLimiting(RateLimitingSetup.GeracaoPdfPolicyName)]
    public async Task<IActionResult> GetAtaRascunhoPdf(int idBanca, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int profId))
            return Unauthorized();

        var ehAvaliadorDaBanca = await _context.BancaAvaliadores
            .AnyAsync(ba => ba.BancaId == idBanca && ba.ProfessorId == profId);

        if (!ehAvaliadorDaBanca)
        {
            _auditLogger.LogWarning(
                "Acesso negado ao rascunho da ata. Solicitante: {SolicitanteId}, BancaId: {BancaId}",
                profId, idBanca);
            // Issue #153 (achado D1/D2): 404 uniforme, não 403 — não confirma a um professor
            // sem vínculo se a banca existe, mesmo padrão do resto do sistema.
            return NotFound("Banca não encontrada ou você não tem permissão para acessar.");
        }

        var resultado = await _ataPdfService.GerarAtaRascunhoAsync(idBanca, cancellationToken);

        if (resultado.Status == AtaPdfResultadoStatus.Sucesso)
        {
            _auditLogger.LogInformation(
                "Download do rascunho da ata concedido. Solicitante: {SolicitanteId}, BancaId: {BancaId}",
                profId, idBanca);
        }

        return resultado.Status switch
        {
            AtaPdfResultadoStatus.Sucesso => File(resultado.PdfBytes!, "application/pdf", $"ata-rascunho-{idBanca}.pdf"),
            AtaPdfResultadoStatus.BancaNaoEncontrada => NotFound("Banca não encontrada."),
            AtaPdfResultadoStatus.ResultadoJaRegistrado => StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado. Utilize o PDF final."),
            AtaPdfResultadoStatus.DadosInconsistentes => this.ErroDadosInconsistentesAtaPdf(),
            _ => StatusCode(StatusCodes.Status500InternalServerError, "Erro inesperado ao gerar o PDF.")
        };
    }
}