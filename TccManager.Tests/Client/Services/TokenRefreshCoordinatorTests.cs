using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Client.Providers;
using TccManager.Client.Services;
using TccManager.Shared.DTOs;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Services;

/// <summary>
/// Issue #75 — <see cref="TokenRefreshCoordinator"/> não tinha nenhum teste dedicado. Não
/// precisa de bUnit (não é um componente Blazor): dublês escritos à mão, mesmo padrão já usado
/// em <see cref="TccManager.Tests.Client.Handlers.AuthTokenHandlerTests"/> (inner handler
/// espião) e <see cref="FakeLocalStorageService"/>.
/// </summary>
public class TokenRefreshCoordinatorTests
{
    private sealed class HandlerEspiao : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HandlerEspiao(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int Chamadas { get; private set; }
        public HttpRequestMessage? UltimaRequisicao { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas++;
            UltimaRequisicao = request;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeSessionEndedHandler : ISessionEndedHandler
    {
        public int Chamadas { get; private set; }

        public Task EncerrarSessaoAsync()
        {
            Chamadas++;
            return Task.CompletedTask;
        }
    }

    private static CustomAuthStateProvider NovoAuthStateProvider(FakeLocalStorageService storage) =>
        // IServiceProvider vazio: NotifyUserAuthentication/NotifyUserLogout (os únicos métodos
        // exercitados aqui) nunca resolvem nada dele — só GetAuthenticationStateAsync usa,
        // fora do escopo destes testes.
        new(storage, new ServiceCollection().BuildServiceProvider());

    private static (TokenRefreshCoordinator Coordinator, HandlerEspiao Espiao, FakeSessionEndedHandler SessionEndedHandler, FakeLocalStorageService Storage)
        Montar(FakeLocalStorageService storage, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var espiao = new HandlerEspiao(responder);
        var factory = new FakeHttpClientFactory(new HttpClient(espiao) { BaseAddress = new Uri("https://localhost:7188/") });
        var sessionEndedHandler = new FakeSessionEndedHandler();
        var coordinator = new TokenRefreshCoordinator(factory, storage, sessionEndedHandler, NovoAuthStateProvider(storage));

        return (coordinator, espiao, sessionEndedHandler, storage);
    }

    [Fact]
    public async Task SemRefreshTokenSalvo_EncerraSessaoSemChamarOServidor()
    {
        var storage = new FakeLocalStorageService();
        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
        Assert.Equal(0, espiao.Chamadas);
    }

    [Fact]
    public async Task OutraRequisicaoJaRenovouEnquantoEsperavaOLock_NaoChamaOServidorDeNovo()
    {
        // Núcleo do single-flight: se authToken em storage já mudou (outra requisição
        // concorrente renovou primeiro), reaproveita sem bater no servidor de novo.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-ja-renovado";
        storage.Store["refreshToken"] = "refresh-abc";
        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var resultado = await coordinator.EnsureRefreshedAsync("token-antigo-usado-na-chamada-que-falhou");

        Assert.True(resultado);
        Assert.Equal(0, espiao.Chamadas);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task RefreshComSucesso_AtualizaStorageEChamaARotaCorreta_SemEncerrarSessao()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        storage.Store["refreshToken"] = "refresh-valido";
        var novoPar = new TokenPairDto { Token = "token-novo", RefreshToken = "refresh-novo" };

        var (coordinator, espiao, sessionEndedHandler, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(novoPar)
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal("token-novo", storage.Store["authToken"]);
        Assert.Equal("refresh-novo", storage.Store["refreshToken"]);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal(HttpMethod.Post, espiao.UltimaRequisicao!.Method);
        Assert.Equal("/api/auth/refresh", espiao.UltimaRequisicao.RequestUri!.AbsolutePath);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task RefreshComSucesso_NotificaOAuthStateProvider()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        storage.Store["refreshToken"] = "refresh-valido";
        var novoPar = new TokenPairDto { Token = "token-novo", RefreshToken = "refresh-novo" };

        var espiao = new HandlerEspiao(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(novoPar) });
        var factory = new FakeHttpClientFactory(new HttpClient(espiao) { BaseAddress = new Uri("https://localhost:7188/") });
        var sessionEndedHandler = new FakeSessionEndedHandler();
        var authStateProvider = NovoAuthStateProvider(storage);

        AuthenticationState? estadoNotificado = null;
        authStateProvider.AuthenticationStateChanged += task => estadoNotificado = task.Result;

        var coordinator = new TokenRefreshCoordinator(factory, storage, sessionEndedHandler, authStateProvider);

        await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.NotNull(estadoNotificado);
    }

    [Fact]
    public async Task RespostaDeErroDoServidor_EncerraSessaoENaoAtualizaStorage()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        storage.Store["refreshToken"] = "refresh-invalido-ou-revogado";
        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal("token-expirado", storage.Store["authToken"]);
    }

    [Theory]
    [InlineData(null, "refresh-novo")]
    [InlineData("", "refresh-novo")]
    [InlineData("token-novo", null)]
    [InlineData("token-novo", "")]
    public async Task RespostaComParDeTokensIncompleto_EncerraSessao(string? token, string? refreshToken)
    {
        // Servidor devolveu 200 mas com corpo malformado/incompleto — não pode deixar a
        // sessão num estado inconsistente (token setado sem refresh, ou vice-versa).
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        storage.Store["refreshToken"] = "refresh-valido";
        var parIncompleto = new TokenPairDto { Token = token ?? "", RefreshToken = refreshToken ?? "" };

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(parIncompleto)
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
    }
}
