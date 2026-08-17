using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Variante da <see cref="TccApiFactory"/> para testar o comportamento do host em função da
/// CONFIGURAÇÃO (issue #64): permite sobrescrever chaves do appsettings e, quando necessário,
/// remover uma seção inteira da configuração efetiva do host — cenário impossível de obter só
/// com <c>UseSetting</c>, que apenas sobrescreve valores, nunca apaga chaves.
///
/// Também captura os eventos de log emitidos pelo host (incluindo os que acontecem entre
/// <c>builder.Build()</c> e <c>app.Run()</c>, como os avisos de <c>App:PublicApiBaseUrl</c> /
/// <c>App:ClientBaseUrl</c>). A captura funciona porque <c>LoggingSetup.ConfigureLogging</c>
/// usa <c>ReadFrom.Services(services)</c>: qualquer <see cref="ILogEventSink"/> registrado no
/// contêiner é anexado ao logger definitivo do host. Cada instância da factory tem seu próprio
/// sink e seu próprio contêiner, portanto não há vazamento entre testes em paralelo.
/// </summary>
public class ConfiguracaoCustomizadaApiFactory : TccApiFactory
{
    private readonly IReadOnlyDictionary<string, string> _configuracoes;
    private readonly string? _prefixoRemovido;
    private readonly CapturaDeLogSink _sink = new();

    /// <param name="configuracoes">
    /// Pares chave/valor aplicados via <c>UseSetting</c> (precedência acima do appsettings.json).
    /// </param>
    /// <param name="prefixoRemovido">
    /// Quando informado, todas as chaves de configuração iniciadas por esse prefixo são
    /// removidas da configuração efetiva (ex.: <c>"Cors:"</c>), preservando todo o restante.
    /// </param>
    public ConfiguracaoCustomizadaApiFactory(
        IReadOnlyDictionary<string, string>? configuracoes = null,
        string? prefixoRemovido = null)
    {
        _configuracoes = configuracoes ?? new Dictionary<string, string>();
        _prefixoRemovido = prefixoRemovido;
    }

    /// <summary>Cópia dos eventos de log emitidos pelo host até o momento da chamada.</summary>
    public IReadOnlyList<LogEvent> LogsDoHost => _sink.Copiar();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        if (_prefixoRemovido is not null)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                // Tira um "retrato" da configuração já montada, descarta as fontes originais e
                // devolve o mesmo conteúdo menos as chaves do prefixo. É o único jeito de
                // simular uma seção ausente sem editar o appsettings.json versionado.
                var atual = (IConfiguration)cfg.Build();

                var semPrefixo = atual.AsEnumerable()
                    .Where(par => par.Value is not null &&
                                  !par.Key.StartsWith(_prefixoRemovido, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(par => par.Key, par => (string?)par.Value);

                cfg.Sources.Clear();
                cfg.AddInMemoryCollection(semPrefixo);
            });
        }

        foreach (var (chave, valor) in _configuracoes)
        {
            builder.UseSetting(chave, valor);
        }

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
