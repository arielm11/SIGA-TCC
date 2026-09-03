using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Fixture da janela de graça de reuso de refresh token (issue #85). Reúne as três capacidades
/// que os testes de <c>AuthTokenService.RefreshAsync</c> precisam ao mesmo tempo e que nenhuma
/// fixture existente oferecia junta:
///
/// <list type="number">
/// <item><b>Relógio controlável</b> (<see cref="Relogio"/>): substitui o <c>TimeProvider.System</c>
/// registrado no <c>Program.cs</c> por um <see cref="RelogioAjustavelTimeProvider"/>. É o que
/// permite sair da janela de 30 s sem esperar 30 s reais (D9 do documento de arquitetura).</item>
/// <item><b>Configuração da janela</b>: <c>Jwt:RefreshReuseGraceSeconds</c> via <c>UseSetting</c>,
/// herdado de <see cref="ConfiguracaoCustomizadaApiFactory"/>. Necessário para o interruptor de
/// reversão (<c>0</c> = comportamento estrito da issue #62, D5/5.3).</item>
/// <item><b>Captura de log</b> (<c>LogsDoHost</c>, herdada): a corrida benigna loga em
/// <c>TccManager.Api.Auditoria</c>/<c>Information</c> e o reuso real em <c>Warning</c> — a
/// distinção entre os dois sinais é metade do objetivo da issue (D8).</item>
/// </list>
///
/// <para><b>Nota sobre o TTL do cache de replay:</b> o <c>IMemoryCache</c> usa o relógio real do
/// processo, não o <see cref="TimeProvider"/> injetado. Avançar <see cref="Relogio"/> tira a
/// reapresentação da janela <i>lógica</i> (a comparação com <c>RevokedAtUtc</c>) sem expirar a
/// entrada de cache. Isso é proposital: isola a regra de classificação do TTL do cache, e é
/// justamente a ordem em que a implementação decide — classifica primeiro, só lê o cache depois.
/// O cache miss tem teste próprio, via <see cref="LimparCacheDeReplay"/>.</para>
/// </summary>
public class RefreshJanelaDeGracaApiFactory : ConfiguracaoCustomizadaApiFactory
{
    /// <summary>Instante inicial do relógio do host. Valor arbitrário e fixo — só precisa ser estável.</summary>
    public static readonly DateTimeOffset InstanteInicial =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Relógio percebido pelo host; avançável dentro do teste.</summary>
    public RelogioAjustavelTimeProvider Relogio { get; } = new(InstanteInicial);

    /// <param name="janelaSegundos">
    /// Valor de <c>Jwt:RefreshReuseGraceSeconds</c>. Quando <c>null</c>, mantém o valor do
    /// <c>appsettings.json</c> (30 s) — o padrão de produção.
    /// </param>
    public RefreshJanelaDeGracaApiFactory(int? janelaSegundos = null)
        : base(MontarConfiguracao(janelaSegundos))
    {
    }

    private static Dictionary<string, string> MontarConfiguracao(int? janelaSegundos) =>
        janelaSegundos is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                ["Jwt:RefreshReuseGraceSeconds"] = janelaSegundos.Value.ToString()
            };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Registro posterior vence em GetRequiredService<TimeProvider>(), substituindo o
        // TimeProvider.System do Program.cs sem tocar no código de produção.
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Relogio));
    }

    /// <summary>
    /// Esvazia o cache de replay do host, simulando cache frio (restart da API, TTL vencido antes
    /// da leitura, ou — no futuro — outra instância sem afinidade). Exercita o fail-safe D6.
    /// </summary>
    public void LimparCacheDeReplay()
    {
        var cache = Services.GetRequiredService<IMemoryCache>();

        if (cache is not MemoryCache memoryCache)
        {
            throw new InvalidOperationException(
                $"IMemoryCache resolvido não é MemoryCache ({cache.GetType().FullName}); " +
                "a fixture precisa ser ajustada para limpar o cache dessa implementação.");
        }

        memoryCache.Clear();
    }
}
