using Microsoft.AspNetCore.Mvc;
using TccManager.Api.Middleware;
using TccManager.Api.Services.Pdf;

namespace TccManager.Api.Extensions;

public static class ControllerBaseExtensions
{
    /// <summary>
    /// Issue #103 (achado do qa-agent): <c>ErroDadosInconsistentesAtaPdf</c> estava duplicado
    /// verbatim em CoordenadorController, AvaliadorController e RascunhoAtaController — só a
    /// cópia do CoordenadorController tinha teste cobrindo o formato da resposta. Extraído
    /// aqui como método de extensão sobre ControllerBase (ver #72/#71 para o raciocínio do
    /// CorrelationId): as 3 chamadas continuam idênticas, agora com uma única implementação.
    /// </summary>
    public static ObjectResult ErroDadosInconsistentesAtaPdf(this ControllerBase controller)
    {
        var correlationId = controller.HttpContext.Items[CorrelationIdMiddleware.ItemsKey] as string;
        return controller.StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Não foi possível gerar a ata.",
            Detail = "Dados da banca inconsistentes. Contate o suporte.",
            Extensions = { ["correlationId"] = correlationId }
        });
    }

    /// <summary>
    /// Issue #155 (achado B2): o switch sobre <see cref="AtaPdfResultadoStatus"/> estava
    /// duplicado em CoordenadorController.GetAtaPdf/GetAtaRascunhoPdf e em
    /// AvaliadorController.GetAtaRascunhoPdf. Os dois fluxos (final/rascunho) nunca retornam
    /// o status do outro na prática (ver AtaPdfService), então reunir os dois casos
    /// (ResultadoNaoRegistrado/409 e ResultadoJaRegistrado/410) num único switch compartilhado
    /// é equivalente ao comportamento anterior de cada chamador — e mais robusto que antes,
    /// caso algum dia um fluxo passe a devolver o status do outro (deixava de cair no 500
    /// genérico do default). RascunhoAtaController.GetRascunhoPorToken tem um switch
    /// parecido, mas com casos/mensagens diferentes (endpoint público, sem nome de arquivo) —
    /// não usa este helper.
    /// </summary>
    public static IActionResult ResultadoAtaPdfParaActionResult(
        this ControllerBase controller, AtaPdfResultado resultado, string nomeArquivo) =>
        resultado.Status switch
        {
            AtaPdfResultadoStatus.Sucesso => controller.File(resultado.PdfBytes!, "application/pdf", nomeArquivo),
            AtaPdfResultadoStatus.BancaNaoEncontrada => controller.NotFound("Banca não encontrada."),
            AtaPdfResultadoStatus.ResultadoNaoRegistrado => controller.Conflict("O resultado desta banca ainda não foi registrado. Gere a ata após registrar a nota final."),
            AtaPdfResultadoStatus.ResultadoJaRegistrado => controller.StatusCode(StatusCodes.Status410Gone, "O resultado desta banca já foi registrado. Utilize o PDF final."),
            AtaPdfResultadoStatus.DadosInconsistentes => controller.ErroDadosInconsistentesAtaPdf(),
            _ => controller.StatusCode(StatusCodes.Status500InternalServerError, "Erro inesperado ao gerar o PDF.")
        };
}
