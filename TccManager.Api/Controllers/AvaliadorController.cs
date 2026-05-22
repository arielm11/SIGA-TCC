using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Professor")]
public class AvaliadorController : ControllerBase
{
    private readonly AppDbContext _context;

    public AvaliadorController(AppDbContext context)
    {
        _context = context;
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
}