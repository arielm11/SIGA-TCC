using Serilog;
using TccManager.Api.Logging;

namespace TccManager.Api.Configuration;

/// <summary>
/// Configuração do Serilog em dois estágios: um bootstrap logger (apenas console),
/// usado antes do host existir para capturar falhas de inicialização, e o logger
/// definitivo, lido a partir do appsettings (RNF3).
/// </summary>
public static class LoggingSetup
{
    public static void CreateBootstrapLogger()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();
    }

    public static WebApplicationBuilder ConfigureLogging(this WebApplicationBuilder builder)
    {
        // preserveStaticLogger: true evita que o logger definitivo (por host) dispute e
        // "congele" o mesmo Log.Logger estático global usado pelo bootstrap — relevante
        // porque múltiplas instâncias de WebApplicationFactory<Program> no mesmo processo
        // (testes de integração) criam múltiplos hosts na mesma execução.
        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Destructure.With<SensitiveDataMaskingPolicy>();
        }, preserveStaticLogger: true);

        return builder;
    }
}
