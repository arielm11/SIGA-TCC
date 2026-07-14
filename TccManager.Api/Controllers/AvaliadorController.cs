using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
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

    [HttpGet("meus-convites")]
    public async Task<IActionResult> GetMeusConvites()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int profId))
            return Unauthorized();

        var convites = await _context.BancaAvaliadores
            .Include(ba => ba.Banca)
                .ThenInclude(b => b!.Tcc)
                    .ThenInclude(t => t!.Aluno)
            .Include(ba => ba.Banca!.Tcc!.Orientador)
            .Include(ba => ba.Banca!.Tcc!.Entregas) 
            .Where(ba => ba.ProfessorId == profId && ba.Banca!.Tcc!.Status != StatusTcc.Finalizado)
            .Select(ba => new ConviteBancaDto
            {
                BancaId = ba.BancaId,
                DataHora = ba.Banca!.DataHora,
                Local = ba.Banca.Local,
                TccTitulo = ba.Banca.Tcc!.Titulo,
                NomeAluno = ba.Banca.Tcc.Aluno!.Nome,
                NomeOrientador = ba.Banca.Tcc.Orientador!.Nome,

                ArquivoFinalCaminho = ba.Banca.Tcc.Entregas
                    .Where(e => e.Tipo == TipoEntrega.Final)
                    .Select(e => e.ArquivoCaminho)
                    .FirstOrDefault() ?? ""
            })
            .OrderBy(c => c.DataHora)
            .ToListAsync();

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
            AtaPdfResultadoStatus.BancaNaoEncontrada => NotFound("Banca não encontrada."),
            AtaPdfResultadoStatus.ResultadoJaRegistrado => StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado. Utilize o PDF final."),
            _ => File(resultado.PdfBytes!, "application/pdf", $"ata-rascunho-{idBanca}.pdf")
        };
    }
}