using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TccManager.Api.Configuration;

/// <summary>
/// Issue #142 (achado M2): o rate limiting das políticas pré-autenticação (login, logout,
/// refresh, troca-senha, rascunho-publico) particiona por <c>Connection.RemoteIpAddress</c> —
/// correto só enquanto a API recebe tráfego diretamente, sem proxy reverso/load balancer na
/// frente. Atrás de um proxy, <c>RemoteIpAddress</c> é sempre o IP do proxy, colapsando a cota
/// de toda a rede/instituição num único bucket (achado A02-2 original). <c>UseForwardedHeaders</c>
/// resolve isso lendo X-Forwarded-For/X-Forwarded-Proto do proxy e reescrevendo
/// <c>HttpContext.Connection.RemoteIpAddress</c>/<c>Request.Scheme</c> — mas só deve confiar
/// nesses cabeçalhos vindos de um proxy conhecido (senão qualquer cliente pode forjar seu
/// próprio IP via X-Forwarded-For).
///
/// Desligado por padrão (<c>ForwardedHeaders:Enabled</c> ausente ou "false"): o ambiente atual
/// (localhost-only, sem proxy) não precisa disso, e habilitar sem configurar
/// KnownProxies/KnownNetworks corretamente é pior que não habilitar (abriria a porta para
/// spoofing de IP via header). Antes de implantar atrás de um proxy reverso/load balancer,
/// configurar via appsettings/variável de ambiente:
/// <code>
/// "ForwardedHeaders": {
///   "Enabled": true,
///   "KnownProxies": ["10.0.0.5"],
///   "KnownNetworks": ["10.0.0.0/24"]
/// }
/// </code>
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection ConfigureForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
            return services;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            foreach (var ip in knownProxies)
            {
                if (IPAddress.TryParse(ip, out var endereco))
                    options.KnownProxies.Add(endereco);
            }

            var knownNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
            foreach (var rede in knownNetworks)
            {
                var partes = rede.Split('/', 2);
                if (partes.Length == 2 && IPAddress.TryParse(partes[0], out var enderecoDeRede) && int.TryParse(partes[1], out var prefixo))
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(enderecoDeRede, prefixo));
            }

            // Sem isso, um proxy encadeado (ex.: Cloudflare -> nginx) que só anexa um salto
            // seria rejeitado por padrão (ForwardLimit default = 1 já cobre o caso comum de 1
            // proxy só; aumentar aqui exigiria configurar explicitamente, fora de escopo desta
            // issue — mantém o padrão conservador do framework).
        });

        return services;
    }

    /// <summary>
    /// Deve ser chamado como o PRIMEIRO middleware do pipeline (antes de
    /// CorrelationIdMiddleware/UseHttpsRedirection/qualquer coisa que leia IP ou scheme) —
    /// mesma exigência documentada pela Microsoft para UseForwardedHeaders. Não-op quando
    /// ForwardedHeaders:Enabled é falso/ausente.
    /// </summary>
    public static IApplicationBuilder UseConfiguredForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
            app.UseForwardedHeaders();

        return app;
    }
}
