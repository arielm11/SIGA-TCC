using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace TccManager.Api.Configuration;

/// <summary>
/// Políticas de rate limiting nomeadas "login", "refresh" e "rascunho-publico": todas usam
/// FixedWindowLimiter, particionado por IP do cliente (Connection.RemoteIpAddress —
/// ambiente é localhost-only, sem proxy reverso, portanto não há tratamento de
/// ForwardedHeaders/X-Forwarded-For) e o mesmo OnRejected (429 + Retry-After + log). Essas
/// três são pré-autenticação por natureza (login ainda não tem usuário; refresh e o
/// rascunho público não exigem token de sessão), então IP é a partição correta para elas.
///
/// "geracao-pdf" é diferente: os 3 endpoints que a usam exigem autenticação, então é
/// particionada por usuário (Claim NameIdentifier), não por IP — ver o comentário na
/// definição da política.
/// </summary>
public static class RateLimitingSetup
{
    public const string LoginPolicyName = "login";
    public const string RefreshPolicyName = "refresh";
    public const string RascunhoPublicoPolicyName = "rascunho-publico";
    public const string GeracaoPdfPolicyName = "geracao-pdf";

    public static IServiceCollection ConfigureRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var loginPermitLimit = configuration.GetValue<int?>("RateLimiting:Login:PermitLimit") ?? 5;
        var loginWindowSeconds = configuration.GetValue<int?>("RateLimiting:Login:WindowSeconds") ?? 60;
        var loginQueueLimit = configuration.GetValue<int?>("RateLimiting:Login:QueueLimit") ?? 0;

        // /refresh acontece em segundo plano com frequência maior que login manual
        // (renovação silenciosa do cliente) — limite mais generoso que "login", mas
        // ainda finito, para preservar o sinal de reuse-detection contra automação.
        var refreshPermitLimit = configuration.GetValue<int?>("RateLimiting:Refresh:PermitLimit") ?? 15;
        var refreshWindowSeconds = configuration.GetValue<int?>("RateLimiting:Refresh:WindowSeconds") ?? 60;
        var refreshQueueLimit = configuration.GetValue<int?>("RateLimiting:Refresh:QueueLimit") ?? 0;

        // O membro externo pode legitimamente recarregar/reabrir o link do rascunho
        // (RF-04/RF-05) — janela um pouco mais folgada que a de "login".
        var rascunhoPermitLimit = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:PermitLimit") ?? 20;
        var rascunhoWindowSeconds = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:WindowSeconds") ?? 60;
        var rascunhoQueueLimit = configuration.GetValue<int?>("RateLimiting:RascunhoPublico:QueueLimit") ?? 0;

        // Geração de PDF de ata (rascunho/final) sob demanda tem custo de CPU/memória não
        // trivial (QuestPDF) — sem limite, um usuário autenticado poderia gerar o mesmo PDF
        // repetidamente e degradar o servidor. Limite mais generoso que os anteriores por
        // ser um endpoint autenticado (não anônimo), não uma superfície de força bruta.
        var geracaoPdfPermitLimit = configuration.GetValue<int?>("RateLimiting:GeracaoPdf:PermitLimit") ?? 20;
        var geracaoPdfWindowSeconds = configuration.GetValue<int?>("RateLimiting:GeracaoPdf:WindowSeconds") ?? 60;
        var geracaoPdfQueueLimit = configuration.GetValue<int?>("RateLimiting:GeracaoPdf:QueueLimit") ?? 0;

        // Fallback para requisição sem usuário autenticado (todos os endpoints desta
        // política são [Authorize], então isso não deveria ser alcançável na prática — mas,
        // se for, fica bem mais restritivo que o limite por usuário, e nunca compartilha a
        // partição de um usuário legítimo).
        var geracaoPdfPermitLimitAnonimo = configuration.GetValue<int?>("RateLimiting:GeracaoPdf:PermitLimitAnonimo") ?? 5;

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

            options.AddPolicy(RefreshPolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = refreshPermitLimit,
                        Window = TimeSpan.FromSeconds(refreshWindowSeconds),
                        QueueLimit = refreshQueueLimit
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

            // Particionado por usuário autenticado (Claim NameIdentifier), não por IP: os 3
            // endpoints desta política exigem autenticação, e a rede de origem típica deste
            // sistema é um campus universitário — particionar por IP faria usuários
            // diferentes atrás do mesmo NAT/proxy compartilharem uma única cota (achado
            // A02-2). Requer UseAuthentication() antes de UseRateLimiter() em Program.cs
            // para que HttpContext.User já esteja populado aqui.
            options.AddPolicy(GeracaoPdfPolicyName, context =>
            {
                var usuarioId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                return usuarioId is not null
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"user:{usuarioId}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = geracaoPdfPermitLimit,
                            Window = TimeSpan.FromSeconds(geracaoPdfWindowSeconds),
                            QueueLimit = geracaoPdfQueueLimit
                        })
                    : RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"anon:{context.Connection.RemoteIpAddress}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = geracaoPdfPermitLimitAnonimo,
                            Window = TimeSpan.FromSeconds(geracaoPdfWindowSeconds),
                            QueueLimit = 0
                        });
            });

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
