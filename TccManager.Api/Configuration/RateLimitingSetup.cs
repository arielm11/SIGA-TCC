using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TccManager.Api.Configuration;

/// <summary>
/// Políticas de rate limiting nomeadas "login" e "rascunho-publico": ambas usam
/// FixedWindowLimiter, particionado por IP do cliente (Connection.RemoteIpAddress —
/// ambiente é localhost-only, sem proxy reverso, portanto não há tratamento de
/// ForwardedHeaders/X-Forwarded-For) e o mesmo OnRejected (429 + Retry-After + log).
/// </summary>
public static class RateLimitingSetup
{
    public const string LoginPolicyName = "login";
    public const string RascunhoPublicoPolicyName = "rascunho-publico";

    public static IServiceCollection ConfigureRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var loginPermitLimit = configuration.GetValue<int?>("RateLimiting:Login:PermitLimit") ?? 5;
        var loginWindowSeconds = configuration.GetValue<int?>("RateLimiting:Login:WindowSeconds") ?? 60;
        var loginQueueLimit = configuration.GetValue<int?>("RateLimiting:Login:QueueLimit") ?? 0;

        // O membro externo pode legitimamente recarregar/reabrir o link do rascunho
        // (RF-04/RF-05) — janela um pouco mais folgada que a de "login".
        var rascunhoPermitLimit = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:PermitLimit") ?? 20;
        var rascunhoWindowSeconds = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:WindowSeconds") ?? 60;
        var rascunhoQueueLimit = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:QueueLimit") ?? 0;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(LoginPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = loginPermitLimit,
                        Window = TimeSpan.FromSeconds(loginWindowSeconds),
                        QueueLimit = loginQueueLimit
                    }));

            options.AddPolicy(RascunhoPublicoPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rascunhoPermitLimit,
                        Window = TimeSpan.FromSeconds(rascunhoWindowSeconds),
                        QueueLimit = rascunhoQueueLimit
                    }));

            options.OnRejected = (context, cancellationToken) =>
            {
                var httpContext = context.HttpContext;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    httpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // Logger obtido via DI (não Serilog.Log estático) para respeitar o logger
                // configurado por host (ver LoggingSetup, preserveStaticLogger: true).
                var logger = httpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("TccManager.Api.RateLimiting");

                logger.LogWarning(
                    "Requisição bloqueada por rate limiting. IP de origem: {RemoteIp}, Rota: {RequestPath}",
                    remoteIp,
                    RedigirPath(httpContext.Request.Path.Value));

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    // O path de /api/rascunho-ata/{token} carrega a credencial de portador no próprio
    // path — nunca deve chegar ao log em claro (rota pública, sem outra forma de logar
    // apenas o template da rota a partir do rate limiter).
    private const string RascunhoAtaPathPrefix = "/api/rascunho-ata/";

    private static string RedigirPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "unknown";

        return path.StartsWith(RascunhoAtaPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"{RascunhoAtaPathPrefix}[REDACTED]"
            : path;
    }
}
