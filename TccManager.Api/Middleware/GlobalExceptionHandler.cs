using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TccManager.Api.Logging;

namespace TccManager.Api.Middleware;

/// <summary>
/// Middleware de exceção global (issue #71): antes deste handler, uma exceção não tratada
/// por um controller propagava até o pipeline do ASP.NET Core, que em Development serializa
/// a página de exceção do desenvolvedor (mensagem, stack trace, valores de configuração) no
/// corpo da resposta — e vários pontos do Client renderizam esse corpo cru para o usuário.
/// Registrado via UseExceptionHandler(), este handler intercepta exceção não tratada por
/// controller/middleware downstream, em qualquer ambiente, e devolve sempre um ProblemDetails
/// genérico — nunca a mensagem da exceção nem stack trace no corpo, mesmo em Development.
///
/// A exceção completa (com stack trace) é sempre logada no servidor, nunca descartada. O
/// CorrelationId é lido de HttpContext.Items (não do header, nem só do LogContext ambiente):
/// o ExceptionHandlerMiddleware chama Response.Clear() — que inclui Headers.Clear() — antes
/// de invocar este handler, apagando o header que CorrelationIdMiddleware já tinha setado
/// diretamente na resposta. Items sobrevive a isso, permitindo reemitir o header e preencher
/// o campo do corpo mesmo numa resposta de erro (achado A09-1,
/// docs/seguranca/2026-08-19-fix-middleware-excecao-global.md).
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = httpContext.Items[CorrelationIdMiddleware.ItemsKey] as string;
        var metodo = httpContext.Request.Method;
        var rota = RequestPathRedactor.Redigir(httpContext.Request.Path.Value);

        // Cliente cancelou a requisição (aba fechada, navegação, timeout do lado dele): não é
        // um erro do servidor, não deve virar Error nem poluir o log de incidentes — e não faz
        // sentido tentar escrever uma resposta para uma conexão que já não existe mais.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Requisição cancelada pelo cliente em {Method} {RequestPath}. CorrelationId: {CorrelationId}",
                metodo, rota, correlationId);
            return true;
        }

        _logger.LogError(
            exception,
            "Exceção não tratada em {Method} {RequestPath}. CorrelationId: {CorrelationId}",
            metodo, rota, correlationId);

        // Best-effort a partir daqui: uma falha AQUI (ex.: escrever num stream já fechado)
        // não pode relançar a exceção ORIGINAL, que reabriria o vazamento de detalhe em
        // Development (o ExceptionHandlerMiddleware relança o erro capturado se o handler
        // lançar) — ver achado A10-1. Nunca passa o RequestAborted para a escrita da resposta
        // de erro: se o cliente cancelou, HasStarted/HasStarted-like guard evita a escrita.
        try
        {
            if (httpContext.Response.HasStarted)
                return true;

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            if (correlationId is not null)
                httpContext.Response.Headers["X-Correlation-Id"] = correlationId;
            httpContext.Response.ContentType = "application/problem+json";

            var problema = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Ocorreu um erro inesperado.",
                Detail = "Tente novamente em instantes. Se o problema persistir, contate o suporte.",
                Extensions = { ["correlationId"] = correlationId }
            };

            // Sem detalhe da exceção no corpo em nenhum ambiente, de propósito — ver
            // comentário da classe. Serialização manual (não WriteAsJsonAsync): aquele helper
            // sobrescreve o Content-Type para "application/json", perdendo o
            // "application/problem+json" (RFC 7807). CancellationToken.None de propósito: se
            // a requisição já foi abortada, não vale a pena propagar o cancelamento para o
            // meio da escrita — melhor terminar a resposta (que pode nem chegar a lugar
            // nenhum) do que arriscar outra exceção no meio do catch de exceção.
            await System.Text.Json.JsonSerializer.SerializeAsync(httpContext.Response.Body, problema, cancellationToken: CancellationToken.None);
        }
        catch (Exception falhaSecundaria)
        {
            _logger.LogWarning(
                falhaSecundaria,
                "Falha ao escrever a resposta de erro em {Method} {RequestPath}. CorrelationId: {CorrelationId}",
                metodo, rota, correlationId);
        }

        return true;
    }
}
