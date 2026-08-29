using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Client.Providers;
using TccManager.Client.Services;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Services;

/// <summary>
/// Issue #75 — <see cref="SessionEndedHandler"/> não tinha nenhum teste dedicado. Usa o
/// <c>BunitNavigationManager</c> do bUnit (já dependência do projeto por causa da issue #75)
/// só como dublê de <see cref="NavigationManager"/> — não renderiza nenhum componente, não é
/// um teste de UI.
/// </summary>
public class SessionEndedHandlerTests
{
    private static CustomAuthStateProvider NovoAuthStateProvider(FakeLocalStorageService storage) =>
        new(storage, new ServiceCollection().BuildServiceProvider());

    [Fact]
    public async Task EncerrarSessaoAsync_RemoveOsDoisTokensDoStorage()
    {
        using var ctx = new BunitContext();
        var navigationManager = ctx.Services.GetRequiredService<NavigationManager>();
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        storage.Store["refreshToken"] = "refresh-atual";
        var handler = new SessionEndedHandler(storage, navigationManager, NovoAuthStateProvider(storage));

        await handler.EncerrarSessaoAsync();

        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.False(storage.Store.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task EncerrarSessaoAsync_NavegaParaLoginComFlagDeExpirado()
    {
        using var ctx = new BunitContext();
        var navigationManager = (Bunit.TestDoubles.BunitNavigationManager)ctx.Services.GetRequiredService<NavigationManager>();
        var storage = new FakeLocalStorageService();
        var handler = new SessionEndedHandler(storage, navigationManager, NovoAuthStateProvider(storage));

        await handler.EncerrarSessaoAsync();

        Assert.EndsWith("/login?expirado=1", navigationManager.Uri);
    }

    [Fact]
    public async Task EncerrarSessaoAsync_NotificaLogoutParaOAuthStateProvider()
    {
        using var ctx = new BunitContext();
        var navigationManager = ctx.Services.GetRequiredService<NavigationManager>();
        var storage = new FakeLocalStorageService();
        var authStateProvider = NovoAuthStateProvider(storage);

        AuthenticationState? estadoNotificado = null;
        authStateProvider.AuthenticationStateChanged += task => estadoNotificado = task.Result;

        var handler = new SessionEndedHandler(storage, navigationManager, authStateProvider);

        await handler.EncerrarSessaoAsync();

        Assert.NotNull(estadoNotificado);
        Assert.False(estadoNotificado!.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task EncerrarSessaoAsync_QuandoNaoHaviaTokenNenhum_NaoLancaExcecao()
    {
        // Sessão já limpa (ex.: dupla chamada concorrente) — RemoveItemAsync numa chave
        // ausente deve ser inofensivo, não lançar.
        using var ctx = new BunitContext();
        var navigationManager = ctx.Services.GetRequiredService<NavigationManager>();
        var storage = new FakeLocalStorageService();
        var handler = new SessionEndedHandler(storage, navigationManager, NovoAuthStateProvider(storage));

        await handler.EncerrarSessaoAsync();

        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.False(storage.Store.ContainsKey("refreshToken"));
    }
}
