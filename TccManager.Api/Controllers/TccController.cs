using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
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
    private readonly IWebHostEnvironment _environment;

    public TccController(AppDbContext context, IWebHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    [HttpGet("meu-tcc")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> GetMeuTcc()
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized("Sessão inválida.");

        var tcc = await _context.Tccs
            .Where(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado)
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
            Titulo = dto.Titulo,
            Resumo = dto.Resumo,
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
    public async Task<IActionResult> GetMinhasEntregas()
    {
        var alunoIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(alunoIdClaim) || !int.TryParse(alunoIdClaim, out int alunoId))
            return Unauthorized();

        var tcc = await _context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == alunoId && t.Status != StatusTcc.Reprovado);
        if (tcc == null) return NotFound("TCC não encontrado.");

        var entregas = await _context.Entregas
            .Where(e => e.TccId == tcc.Id)
            .OrderByDescending(e => e.DataEnvio)
            .ToListAsync();

        return Ok(entregas);
    }

    [HttpPost("entregas")]
    [Authorize(Roles = "Aluno")]
    public async Task<IActionResult> EnviarEntrega([FromForm] string tituloEntrega, [FromForm] TipoEntrega tipo, IFormFile arquivo)
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

        if (arquivo == null || arquivo.Length == 0)
            return BadRequest("Nenhum arquivo enviado.");

        var extensoesPermitidas = new[] { ".pdf", ".doc", ".docx", ".zip" };
        var extensao = Path.GetExtension(arquivo.FileName).ToLowerInvariant();

        if (!extensoesPermitidas.Contains(extensao))
            return BadRequest("Formato de arquivo não permitido. Envie apenas PDF, DOC, DOCX ou ZIP.");

        if (tipo == TipoEntrega.Final && tcc.OrientadorId == null)
            return BadRequest("Você não pode enviar a versão FINAL sem ter um Orientador definido (RN03).");

        var uploadsFolder = Path.Combine(_environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads", "entregas");
        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

        var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(arquivo.FileName)}";
        var filePath = Path.Combine(uploadsFolder, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await arquivo.CopyToAsync(stream);
        }

        var entrega = new Entrega
        {
            TccId = tcc.Id,
            Titulo = tituloEntrega,
            ArquivoCaminho = $"/uploads/entregas/{fileName}",
            Tipo = tipo,
            DataEnvio = DateTime.UtcNow
        };

        _context.Entregas.Add(entrega);
        await _context.SaveChangesAsync();

        return Ok(entrega);
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