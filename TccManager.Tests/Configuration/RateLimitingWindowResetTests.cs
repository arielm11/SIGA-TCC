using System.Net;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #75 ("teste de reset da janela de rate limiting (60s)") — nenhum teste existente
/// provava que a janela do <c>FixedWindowLimiter</c> de fato reabre depois de expirar; todos só
/// checavam o bloqueio dentro da mesma janela. Em vez de esperar os 60s reais de produção
/// (lento e ainda assim só prova o mesmo mecanismo), derruba <c>WindowSeconds</c> para um valor
/// pequeno via <see cref="ConfiguracaoCustomizadaApiFactory"/> e espera passar da janela de
/// verdade — sem fake clock: confirmado que <c>FixedWindowRateLimiterOptions</c> (versão do
/// <c>System.Threading.RateLimiting</c> empacotada com o ASP.NET Core 9 usada neste projeto)
/// não expõe um <c>TimeProvider</c> injetável, então esse é o único jeito de exercitar o reset
/// de verdade sem reimplementar o limitador.
///
/// Isolamento por IP distinto não é coberto aqui: as políticas particionadas por IP (login,
/// refresh, rascunho-publico) sempre veem a mesma origem dentro do <c>TestServer</c> em
/// memória (sem um proxy real na frente), então não há como simular dois IPs de cliente
/// diferentes batendo na mesma instância de teste — limitação do ambiente de teste, registrada
/// em docs/implementacao, não uma lacuna resolvida aqui.
/// </summary>
public class RateLimitingWindowResetTests
{
    private const int IdCoordenador = 1;

    [Fact]
    public async Task AposAJanelaExpirar_VoltaAAceitarRequisicoes()
    {
        var factory = new ConfiguracaoCustomizadaApiFactory(new Dictionary<string, string>
        {
            ["RateLimiting:ListagemPaginada:PermitLimit"] = "1",
            ["RateLimiting:ListagemPaginada:WindowSeconds"] = "2"
        });
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var primeira = await client.GetAsync("/api/coordenador/professores");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, primeira.StatusCode);

        var bloqueada = await client.GetAsync("/api/coordenador/professores");
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);

        // Espera passar da janela de 2s de verdade (com folga) — sem fake clock disponível
        // nesta versão do limitador (ver doc da classe).
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var depoisDaJanela = await client.GetAsync("/api/coordenador/professores");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, depoisDaJanela.StatusCode);
    }

    [Fact]
    public async Task DentroDaMesmaJanela_ContinuaBloqueando()
    {
        // Contraprova do teste acima: sem esperar a janela expirar, o bloqueio persiste — não
        // é um limitador que "esquece" sozinho a cada requisição.
        var factory = new ConfiguracaoCustomizadaApiFactory(new Dictionary<string, string>
        {
            ["RateLimiting:ListagemPaginada:PermitLimit"] = "1",
            ["RateLimiting:ListagemPaginada:WindowSeconds"] = "30"
        });
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        await client.GetAsync("/api/coordenador/professores");

        var segunda = await client.GetAsync("/api/coordenador/professores");
        var terceira = await client.GetAsync("/api/coordenador/professores");

        Assert.Equal(HttpStatusCode.TooManyRequests, segunda.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, terceira.StatusCode);
    }
}
