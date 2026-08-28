using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TccManager.Api.Logging;

namespace TccManager.Api.Configuration;

/// <summary>
/// Políticas de rate limiting nomeadas "login", "refresh" e "rascunho-publico": todas usam
/// FixedWindowLimiter, particionado por IP do cliente (Connection.RemoteIpAddress —
/// ambiente é localhost-only, sem proxy reverso, portanto não há tratamento de
/// ForwardedHeaders/X-Forwarded-For) e o mesmo OnRejected (429 + Retry-After + log). Essas
/// três são pré-autenticação por natureza (login ainda não tem usuário; refresh e o
/// rascunho público não exigem token de sessão), então IP é a partição correta para elas.
///
/// "geracao-pdf" e "listagem-paginada" são diferentes: os endpoints que as usam exigem
/// autenticação, então são particionadas por usuário (Claim NameIdentifier), não por IP —
/// ver o comentário na definição de cada uma.
/// </summary>
public static class RateLimitingSetup
{
    public const string LoginPolicyName = "login";
    public const string RefreshPolicyName = "refresh";
    public const string RascunhoPublicoPolicyName = "rascunho-publico";
    public const string GeracaoPdfPolicyName = "geracao-pdf";
    public const string ListagemPaginadaPolicyName = "listagem-paginada";

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

        // Issue #74: endpoints de listagem paginada (professores, membros externos, bancas
        // concluídas, dashboard do orientador, minhas entregas) não tinham rate limiting nem
        // log de rejeição. O valor real desta política é limitar custo de CPU/banco por
        // usuário e gerar sinal de log para investigação — não impedir enumeração completa
        // do catálogo por si só (com MaxPageSize=100, 60 req/min ainda permite ler bases de
        // porte pequeno/médio dentro de uma janela; ver achado da revisão de segurança,
        // docs/seguranca/2026-08-27-paginacao-cancellation-rate-limiting.md). Limite bem mais
        // generoso que "geracao-pdf" (listar é leve; paginação/navegação legítima pode
        // disparar várias chamadas em sequência rápida), mas ainda finito.
        var listagemPaginadaPermitLimit = configuration.GetValue<int?>("RateLimiting:ListagemPaginada:PermitLimit") ?? 60;
        var listagemPaginadaWindowSeconds = configuration.GetValue<int?>("RateLimiting:ListagemPaginada:WindowSeconds") ?? 60;
        var listagemPaginadaQueueLimit = configuration.GetValue<int?>("RateLimiting:ListagemPaginada:QueueLimit") ?? 0;

        // Mesmo raciocínio do fallback de "geracao-pdf": todos os endpoints desta política
        // são [Authorize], então este ramo não deveria ser alcançável por uma requisição que
        // chegue à ação — mas UseRateLimiter() roda antes de UseAuthorization() em
        // Program.cs, então uma requisição SEM token válido ainda é avaliada pelo limitador
        // antes de ser rejeitada com 401. Fica mais restritivo que o limite por usuário, e
        // nunca compartilha a partição de um usuário legítimo.
        var listagemPaginadaPermitLimitAnonimo = configuration.GetValue<int?>("RateLimiting:ListagemPaginada:PermitLimitAnonimo") ?? 10;

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

            // Mesmo raciocínio de particionamento de "geracao-pdf" (achado A02-2): os 6
            // endpoints desta política exigem autenticação, então particiona por usuário
            // (Claim NameIdentifier), não por IP — a rede de origem típica é um campus
            // universitário, e particionar por IP faria usuários diferentes atrás do mesmo
            // NAT/proxy compartilharem uma única cota.
            options.AddPolicy(ListagemPaginadaPolicyName, context =>
            {
                var usuarioId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                return usuarioId is not null
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"user:{usuarioId}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = listagemPaginadaPermitLimit,
                            Window = TimeSpan.FromSeconds(listagemPaginadaWindowSeconds),
                            QueueLimit = listagemPaginadaQueueLimit
                        })
                    : RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: $"anon:{context.Connection.RemoteIpAddress}",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = listagemPaginadaPermitLimitAnonimo,
                            Window = TimeSpan.FromSeconds(listagemPaginadaWindowSeconds),
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

                // Issue #74 (achado F-07 da revisão de segurança): políticas particionadas
                // por usuário autenticado (geracao-pdf, listagem-paginada) tornam o IP sozinho
                // pouco acionável — a rede de origem típica é um campus universitário atrás de
                // NAT/proxy, então o IP não identifica quem excedeu a cota. Inclui o
                // UsuarioId quando disponível (requisição autenticada); "anon" caso contrário.
                var usuarioId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anon";

                // Logger obtido via DI (não Serilog.Log estático) para respeitar o logger
                // configurado por host (ver LoggingSetup, preserveStaticLogger: true).
                var logger = httpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("TccManager.Api.RateLimiting");

                logger.LogWarning(
                    "Requisição bloqueada por rate limiting. IP de origem: {RemoteIp}, UsuarioId: {UsuarioId}, Rota: {RequestPath}",
                    remoteIp,
                    usuarioId,
                    RequestPathRedactor.Redigir(httpContext.Request.Path.Value));

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}
