using Microsoft.AspNetCore.Mvc;
using TccManager.Api.Middleware;

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
}
