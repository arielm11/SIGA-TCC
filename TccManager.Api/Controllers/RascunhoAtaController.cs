using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TccManager.Api.Configuration;
using TccManager.Api.Services.Pdf;

namespace TccManager.Api.Controllers;

/// <summary>
/// Endpoint público (RF-04/Etapa 2): acesso do avaliador externo ao PDF rascunho via
/// token opaco, sem exigir cadastro/login (<c>MembroExterno</c> não tem conta no sistema).
/// Classe inteira sem <see cref="AuthorizeAttribute"/> — o token é o único fator de acesso.
/// </summary>
[ApiController]
[Route("api/rascunho-ata")]
[AllowAnonymous]
public class RascunhoAtaController : ControllerBase
{
    private readonly IRascunhoAtaTokenService _tokenService;
    private readonly IAtaPdfService _ataPdfService;

    // Categoria dedicada de auditoria — ver TccController para o motivo (MinimumLevel padrão
    // do Serilog é Warning; "TccManager.Api.Auditoria" tem Override para Information).
    private readonly ILogger _auditLogger;

    public RascunhoAtaController(
        IRascunhoAtaTokenService tokenService,
        IAtaPdfService ataPdfService,
        ILoggerFactory loggerFactory)
    {
        _tokenService = tokenService;
        _ataPdfService = ataPdfService;
        _auditLogger = loggerFactory.CreateLogger("TccManager.Api.Auditoria");
    }

    [HttpGet("{token}")]
    [EnableRateLimiting(RateLimitingSetup.RascunhoPublicoPolicyName)]
    public async Task<IActionResult> GetRascunhoPorToken(string token)
    {
        var validacao = await _tokenService.ValidarAsync(token);

        // Auditoria de acesso: nunca o token em si (credencial de portador) nem o e-mail
        // do membro externo — apenas os ids envolvidos e o desfecho da validação.
        if (validacao.Status == RascunhoTokenValidacaoStatus.Invalido)
        {
            _auditLogger.LogWarning(
                "Acesso ao rascunho da ata via token inválido/expirado. BancaId: {BancaId}, MembroExternoId: {MembroExternoId}",
                validacao.BancaId,
                validacao.MembroExternoId);
        }
        else
        {
            _auditLogger.LogInformation(
                "Acesso ao rascunho da ata via token válido. BancaId: {BancaId}, MembroExternoId: {MembroExternoId}, Status: {Status}",
                validacao.BancaId,
                validacao.MembroExternoId,
                validacao.Status);
        }

        if (validacao.Status == RascunhoTokenValidacaoStatus.Invalido)
            return NotFound("Link inválido ou expirado.");

        if (validacao.Status == RascunhoTokenValidacaoStatus.ResultadoRegistrado)
            return StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado; o rascunho não está mais disponível.");

        var resultado = await _ataPdfService.GerarAtaRascunhoAsync(validacao.BancaId);

        // Servido inline (sem Content-Disposition: attachment) — o membro externo abre
        // o PDF direto no navegador a partir do link recebido por e-mail (RF-07).
        return resultado.Status switch
        {
            AtaPdfResultadoStatus.Sucesso => File(resultado.PdfBytes!, "application/pdf"),
            AtaPdfResultadoStatus.ResultadoJaRegistrado => StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado; o rascunho não está mais disponível."),
            _ => NotFound("Link inválido ou expirado.")
        };
    }
}
