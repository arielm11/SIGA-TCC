using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TccManager.Api.Configuration;

/// <summary>
/// Política de rate limiting nomeada "login": FixedWindowLimiter, particionado por
/// IP do cliente (Connection.RemoteIpAddress — ambiente é localhost-only, sem proxy
/// reverso, portanto não há tratamento de ForwardedHeaders/X-Forwarded-For).
/// </summary>
public static class RateLimitingSetup
{
    public const string LoginPolicyName = "login";

    public static IServiceCollection ConfigureRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue<int?>("RateLimiting:Login:PermitLimit") ?? 5;
        var windowSeconds = configuration.GetValue<int?>("RateLimiting:Login:WindowSeconds") ?? 60;
        var queueLimit = configuration.GetValue<int?>("RateLimiting:Login:QueueLimit") ?? 0;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(LoginPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = queueLimit
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
                    "Tentativa de login bloqueada por rate limiting. IP de origem: {RemoteIp}, Rota: {RequestPath}",
                    remoteIp,
                    httpContext.Request.Path.Value);

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}
