using Serilog.Context;

namespace TccManager.Api.Middleware;

public class CorrelationIdMiddleware
{
    // Chave pública (usada por GlobalExceptionHandler para reemitir o header em respostas de
    // erro — ver comentário abaixo sobre Response.Clear()).
    public const string ItemsKey = "CorrelationId";

    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing) && Guid.TryParse(existing, out var parsed)
            ? parsed.ToString()
            : Guid.NewGuid().ToString();

        // Também em HttpContext.Items, não só no header: o ExceptionHandlerMiddleware chama
        // Response.Clear() (que inclui Headers.Clear()) antes de invocar o handler de
        // exceção, apagando o header setado aqui. Items sobrevive a isso — é o que permite
        // GlobalExceptionHandler reemitir o CorrelationId numa resposta de erro (achado
        // A09-1, docs/seguranca/2026-08-19-fix-middleware-excecao-global.md).
        context.Items[ItemsKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
