using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Combina as duas capacidades que a auditoria de <c>POST api/tcc/entregas</c> exige ao mesmo
/// tempo e que nenhuma fixture existente oferecia junto (issue #82, D6):
/// <list type="bullet">
/// <item>isolamento do <c>WebRootPath</c> (<see cref="WebRootIsolatedApiFactory"/>), sem o qual o
/// arquivo do upload iria parar no wwwroot real do projeto <c>TccManager.Api</c>;</item>
/// <item>captura dos <see cref="LogEvent"/> emitidos pelo host, hoje disponível apenas em
/// <see cref="ConfiguracaoCustomizadaApiFactory"/> — que herda da <see cref="TccApiFactory"/>
/// e portanto NÃO isola o web root.</item>
/// </list>
///
/// A captura funciona pelo mesmo motivo documentado em <see cref="ConfiguracaoCustomizadaApiFactory"/>:
/// <c>LoggingSetup.ConfigureLogging</c> usa <c>ReadFrom.Services(services)</c>, então qualquer
/// <see cref="ILogEventSink"/> registrado no contêiner é anexado ao logger definitivo do host. Cada
/// instância da factory tem seu próprio sink e seu próprio contêiner — sem vazamento entre testes
/// em paralelo.
///
/// A categoria <c>TccManager.Api.Auditoria</c> tem <c>Override</c> explícito para
/// <c>Information</c> no <c>appsettings.json</c>, então os eventos de auditoria chegam ao sink
/// mesmo com o <c>MinimumLevel.Default</c> em <c>Warning</c>.
/// </summary>
public class WebRootIsolatedComCapturaDeLogApiFactory : WebRootIsolatedApiFactory
{
    private readonly CapturaDeLogSink _sink = new();

    /// <summary>Cópia dos eventos de log emitidos pelo host até o momento da chamada.</summary>
    public IReadOnlyList<LogEvent> LogsDoHost => _sink.Copiar();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(_sink));
    }

    private sealed class CapturaDeLogSink : ILogEventSink
    {
        private readonly List<LogEvent> _eventos = [];

        public void Emit(LogEvent logEvent)
        {
            lock (_eventos)
            {
                _eventos.Add(logEvent);
            }
        }

        public IReadOnlyList<LogEvent> Copiar()
        {
            lock (_eventos)
            {
                return _eventos.ToList();
            }
        }
    }
}
