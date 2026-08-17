using System.Net;
using Serilog.Events;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #64 — <c>App:PublicApiBaseUrl</c> e <c>App:ClientBaseUrl</c> não têm valor padrão
/// sensato e, vazias, quebram apenas os links montados dentro de e-mails
/// (<c>TccNotificationService</c>, que engole as próprias falhas). A decisão registrada foi
/// avisar no startup, NÃO derrubar a aplicação (nada de fail-fast).
///
/// Os avisos são emitidos entre <c>builder.Build()</c> e <c>app.Run()</c>, por
/// <c>app.Logger</c>. A <see cref="ConfiguracaoCustomizadaApiFactory"/> consegue capturá-los
/// porque o logger definitivo do host anexa os <c>ILogEventSink</c> registrados no contêiner
/// (<c>ReadFrom.Services</c> em <c>LoggingSetup</c>).
///
/// Todos os testes fixam explicitamente os dois valores via <c>UseSetting</c>: o
/// appsettings.Development.json não é versionado (está no .gitignore) e poderia,
/// em uma máquina qualquer, já trazer as URLs preenchidas.
/// </summary>
public class AppUrlsStartupWarningTests
{
    private const string ChavePublicApi = "App:PublicApiBaseUrl";
    private const string ChaveClient = "App:ClientBaseUrl";

    private static ConfiguracaoCustomizadaApiFactory Host(string publicApiBaseUrl, string clientBaseUrl) =>
        new(new Dictionary<string, string>
        {
            [ChavePublicApi] = publicApiBaseUrl,
            [ChaveClient] = clientBaseUrl
        });

    private static IReadOnlyList<LogEvent> Avisos(ConfiguracaoCustomizadaApiFactory factory, string chave) =>
        factory.LogsDoHost
            .Where(evento => evento.Level == LogEventLevel.Warning &&
                             evento.RenderMessage().Contains(chave, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void AmbasVazias_LogaUmWarningParaCadaChave()
    {
        using var factory = Host(string.Empty, string.Empty);
        factory.CreateClient(); // dispara a construção e a inicialização do host

        Assert.Single(Avisos(factory, ChavePublicApi));
        Assert.Single(Avisos(factory, ChaveClient));
    }

    [Fact]
    public void AmbasPreenchidas_NaoLogaNenhumWarningDeConfiguracao()
    {
        using var factory = Host("https://api.exemplo.edu.br", "https://app.exemplo.edu.br");
        factory.CreateClient();

        Assert.Empty(Avisos(factory, ChavePublicApi));
        Assert.Empty(Avisos(factory, ChaveClient));
    }

    [Fact]
    public void ApenasPublicApiBaseUrlVazia_LogaSomenteOAvisoDela()
    {
        using var factory = Host(string.Empty, "https://app.exemplo.edu.br");
        factory.CreateClient();

        Assert.Single(Avisos(factory, ChavePublicApi));
        Assert.Empty(Avisos(factory, ChaveClient));
    }

    [Fact]
    public void ApenasClientBaseUrlVazia_LogaSomenteOAvisoDela()
    {
        using var factory = Host("https://api.exemplo.edu.br", string.Empty);
        factory.CreateClient();

        Assert.Empty(Avisos(factory, ChavePublicApi));
        Assert.Single(Avisos(factory, ChaveClient));
    }

    [Fact]
    public void ValorApenasComEspacos_ContaComoNaoConfigurada()
    {
        // A checagem é IsNullOrWhiteSpace, não IsNullOrEmpty: "   " não é uma URL utilizável.
        using var factory = Host("   ", "\t");
        factory.CreateClient();

        Assert.Single(Avisos(factory, ChavePublicApi));
        Assert.Single(Avisos(factory, ChaveClient));
    }

    [Fact]
    public async Task UrlsVazias_NaoImpedemAAplicacaoDeSubirEAtender()
    {
        // Trava a decisão de "warning, não fail-fast": com as duas URLs vazias a API continua
        // subindo e respondendo normalmente. Uma mudança para fail-fast quebraria este teste
        // (e deve, nesse caso, ser uma decisão deliberada).
        using var factory = Host(string.Empty, string.Empty);
        var client = factory.CreateClient();

        var resposta = await client.GetAsync("/api/usuario/me");

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }
}
