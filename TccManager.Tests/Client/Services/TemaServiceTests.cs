using Microsoft.JSInterop;
using TccManager.Client.Services;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Services;

/// <summary>
/// Testes unitários de <see cref="TemaService"/> isolando sua lógica pura (resolução do tema a
/// partir do localStorage, alternância, persistência e disparo de evento) com fakes escritos à
/// mão de <see cref="Blazored.LocalStorage.ILocalStorageService"/> e <see cref="IJSRuntime"/>.
/// Não cobre o script anti-flash de index.html nem o theme.js real (JS puro — fora do alcance
/// sem bUnit/ambiente de navegador; ver docs/testes).
/// </summary>
public class TemaServiceTests
{
    private static TemaService CriarServico(out FakeLocalStorageService storage, out FakeJsRuntime js)
    {
        storage = new FakeLocalStorageService();
        js = new FakeJsRuntime();
        return new TemaService(storage, js);
    }

    // ── Estado inicial ────────────────────────────────────────────────

    [Fact]
    public void TemaAtual_AntesDeInicializar_EhClaroPorPadrao()
    {
        var service = CriarServico(out _, out _);

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
        Assert.False(service.EhEscuro);
    }

    // ── InicializarAsync ──────────────────────────────────────────────

    [Fact]
    public async Task InicializarAsync_ChaveAusente_MantemTemaClaro()
    {
        var service = CriarServico(out _, out _);

        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
        Assert.False(service.EhEscuro);
    }

    [Fact]
    public async Task InicializarAsync_ValorEscuroSemAspas_DefineTemaEscuro()
    {
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "standard-dark";

        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaEscuro, service.TemaAtual);
        Assert.True(service.EhEscuro);
    }

    [Fact]
    public async Task InicializarAsync_ValorEscuroComAspasJson_DefineTemaEscuro()
    {
        // Blazored grava strings como JSON (entre aspas); InicializarAsync deve remover as aspas
        // antes de comparar — mesmo padrão .Replace("\"","") usado para authToken no projeto.
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "\"standard-dark\"";

        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaEscuro, service.TemaAtual);
    }

    [Fact]
    public async Task InicializarAsync_ValorClaro_DefineTemaClaro()
    {
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "standard";

        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
    }

    [Fact]
    public async Task InicializarAsync_ValorDesconhecido_CaiParaTemaClaro()
    {
        // Regra documentada: qualquer valor que não seja exatamente "standard-dark" resolve claro.
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "material";

        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
    }

    [Fact]
    public async Task InicializarAsync_ValorEscuroQueJaTinhaSidoDefinido_PermaneceEscuro()
    {
        // Idempotência: reinicializar com o mesmo valor persistido não altera o estado.
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "standard-dark";

        await service.InicializarAsync();
        await service.InicializarAsync();

        Assert.Equal(TemaService.TemaEscuro, service.TemaAtual);
    }

    // ── AlternarTemaAsync — transição de estado ───────────────────────

    [Fact]
    public async Task AlternarTemaAsync_DeClaroParaEscuro_AtualizaEstado()
    {
        var service = CriarServico(out _, out _);

        await service.AlternarTemaAsync();

        Assert.Equal(TemaService.TemaEscuro, service.TemaAtual);
        Assert.True(service.EhEscuro);
    }

    [Fact]
    public async Task AlternarTemaAsync_DeEscuroParaClaro_AtualizaEstado()
    {
        var service = CriarServico(out var storage, out _);
        storage.Store["preferenciaTema"] = "standard-dark";
        await service.InicializarAsync();

        await service.AlternarTemaAsync();

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
        Assert.False(service.EhEscuro);
    }

    [Fact]
    public async Task AlternarTemaAsync_DuasVezes_VoltaAoTemaOriginal()
    {
        var service = CriarServico(out _, out _);

        await service.AlternarTemaAsync();
        await service.AlternarTemaAsync();

        Assert.Equal(TemaService.TemaClaro, service.TemaAtual);
    }

    // ── AlternarTemaAsync — persistência e interop ────────────────────

    [Fact]
    public async Task AlternarTemaAsync_PersisteNovoTemaNoLocalStorage()
    {
        var service = CriarServico(out var storage, out _);

        await service.AlternarTemaAsync();

        Assert.Equal(TemaService.TemaEscuro, storage.Store["preferenciaTema"]);
    }

    [Fact]
    public async Task AlternarTemaAsync_InvocaSetRadzenThemeComTemaAtual()
    {
        var service = CriarServico(out _, out var js);

        await service.AlternarTemaAsync();

        var invocacao = Assert.Single(js.Invocacoes);
        Assert.Equal("setRadzenTheme", invocacao.Identifier);
        Assert.NotNull(invocacao.Args);
        Assert.Equal(TemaService.TemaEscuro, Assert.Single(invocacao.Args!));
    }

    [Fact]
    public async Task AlternarTemaAsync_DeEscuroParaClaro_InvocaSetRadzenThemeComClaro()
    {
        var service = CriarServico(out var storage, out var js);
        storage.Store["preferenciaTema"] = "standard-dark";
        await service.InicializarAsync();

        await service.AlternarTemaAsync();

        var invocacao = Assert.Single(js.Invocacoes);
        Assert.Equal("setRadzenTheme", invocacao.Identifier);
        Assert.Equal(TemaService.TemaClaro, Assert.Single(invocacao.Args!));
    }

    [Fact]
    public async Task AlternarTemaAsync_PersisteAntesDeInvocarInterop_NaOrdemEsperada()
    {
        // Garante que o estado persistido e o argumento do interop são coerentes entre si.
        var service = CriarServico(out var storage, out var js);

        await service.AlternarTemaAsync();

        Assert.Equal(storage.Store["preferenciaTema"], Assert.Single(js.Invocacoes).Args!.Single());
    }

    // ── AlternarTemaAsync — evento ────────────────────────────────────

    [Fact]
    public async Task AlternarTemaAsync_DisparaEventoTemaAlterado()
    {
        var service = CriarServico(out _, out _);
        var disparos = 0;
        service.TemaAlterado += () => disparos++;

        await service.AlternarTemaAsync();

        Assert.Equal(1, disparos);
    }

    [Fact]
    public async Task AlternarTemaAsync_SemAssinantes_NaoLancaExcecao()
    {
        var service = CriarServico(out _, out _);

        var excecao = await Record.ExceptionAsync(() => service.AlternarTemaAsync());

        Assert.Null(excecao);
    }

    [Fact]
    public async Task AlternarTemaAsync_ChamadoDuasVezes_DisparaEventoParaCadaChamada()
    {
        var service = CriarServico(out _, out _);
        var disparos = 0;
        service.TemaAlterado += () => disparos++;

        await service.AlternarTemaAsync();
        await service.AlternarTemaAsync();

        Assert.Equal(2, disparos);
    }
}
