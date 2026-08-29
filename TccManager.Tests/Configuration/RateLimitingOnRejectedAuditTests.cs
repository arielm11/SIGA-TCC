using System.Net;
using Serilog.Events;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #75 ("auditoria do OnRejected só verificada por inspeção manual") — todos os testes
/// de rate limiting existentes checavam só a resposta HTTP (429 + Retry-After), nunca o
/// conteúdo real da linha de log emitida pelo <c>OnRejected</c> compartilhado em
/// <c>RateLimitingSetup.cs</c>. Usa <see cref="ConfiguracaoCustomizadaApiFactory"/> para
/// derrubar o limite da política "listagem-paginada" para 1/60s (em vez de esperar 60
/// requisições reais) e capturar o log do host via <c>LogsDoHost</c>.
/// </summary>
public class RateLimitingOnRejectedAuditTests
{
    private const int IdCoordenador = 1;

    [Fact]
    public async Task RequisicaoBloqueada_LogaWarningComUsuarioIdEIpERotaRedigida()
    {
        var factory = new ConfiguracaoCustomizadaApiFactory(new Dictionary<string, string>
        {
            ["RateLimiting:ListagemPaginada:PermitLimit"] = "1",
            ["RateLimiting:ListagemPaginada:WindowSeconds"] = "60"
        });
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var primeira = await client.GetAsync("/api/coordenador/professores");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, primeira.StatusCode);

        var bloqueada = await client.GetAsync("/api/coordenador/professores");
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);

        var entrada = Assert.Single(factory.LogsDoHost, e =>
            e.RenderMessage().Contains("bloqueada por rate limiting", StringComparison.Ordinal));

        Assert.Equal(LogEventLevel.Warning, entrada.Level);

        var mensagem = entrada.RenderMessage();
        Assert.Contains("UsuarioId", mensagem, StringComparison.Ordinal);
        Assert.Contains($"{IdCoordenador}", mensagem, StringComparison.Ordinal);
        Assert.Contains("/api/coordenador/professores", mensagem, StringComparison.Ordinal);

        Assert.True(entrada.Properties.ContainsKey("UsuarioId"), "UsuarioId deveria ser uma propriedade estruturada, não só texto solto na mensagem.");
        Assert.Equal($"\"{IdCoordenador}\"", entrada.Properties["UsuarioId"].ToString());
    }

    [Fact]
    public async Task RequisicaoBloqueadaSemAutenticacao_LogaUsuarioIdComoAnon()
    {
        var factory = new ConfiguracaoCustomizadaApiFactory(new Dictionary<string, string>
        {
            ["RateLimiting:ListagemPaginada:PermitLimitAnonimo"] = "1",
            ["RateLimiting:ListagemPaginada:WindowSeconds"] = "60"
        });
        using var _ = factory;
        var client = factory.CreateClient();

        // Sem token válido: cai no ramo "anon:{IP}" da política (UseRateLimiter roda antes de
        // UseAuthorization) — a segunda requisição já deveria ser bloqueada com limite 1.
        await client.GetAsync("/api/coordenador/professores");
        var bloqueada = await client.GetAsync("/api/coordenador/professores");

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);

        var entrada = Assert.Single(factory.LogsDoHost, e =>
            e.RenderMessage().Contains("bloqueada por rate limiting", StringComparison.Ordinal));

        Assert.Equal("\"anon\"", entrada.Properties["UsuarioId"].ToString());
    }
}
