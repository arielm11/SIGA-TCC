using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Middleware;
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

    public AvaliadorController(AppDbContext context, IAtaPdfService ataPdfService)
    {
        _context = context;
        _ataPdfService = ataPdfService;
    }

    // Issue #72: mesmo raciocínio do CorrelationId em GlobalExceptionHandler (issue #71) —
    // sem ele, "contate o suporte" não dá ao suporte como localizar o log com o motivo
    // específico da inconsistência.
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

    [HttpGet("meus-convites")]
    public async Task<IActionResult> GetMeusConvites()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int profId))
            return Unauthorized();

        // A extensão real (.pdf/.doc/.docx/.zip) não é calculável em SQL — Path.GetExtension
        // roda em memória sobre o caminho já materializado, então a projeção fica em duas
        // etapas: busca o caminho da Entrega Final (SQL) e só depois deriva a extensão (C#).
        var convitesBrutos = await _context.BancaAvaliadores
            .Include(ba => ba.Banca)
                .ThenInclude(b => b!.Tcc)
                    .ThenInclude(t => t!.Aluno)
            .Include(ba => ba.Banca!.Tcc!.Orientador)
            .Include(ba => ba.Banca!.Tcc!.Entregas)
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
    public async Task<IActionResult> GetAtaRascunhoPdf(int idBanca)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int profId))
            return Unauthorized();

        var ehAvaliadorDaBanca = await _context.BancaAvaliadores
            .AnyAsync(ba => ba.BancaId == idBanca && ba.ProfessorId == profId);

        if (!ehAvaliadorDaBanca)
            return Forbid();

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
}