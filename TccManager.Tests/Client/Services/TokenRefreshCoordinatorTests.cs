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
/// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, §6.2/6.3) —
/// reescrita completa depois da migração do refresh token para cookie HttpOnly: o sinal de
/// "outra aba já renovou" passa a ser o <c>authToken</c> (não mais o <c>refreshToken</c>, que o
/// Client não tem mais acesso a). Não precisa de bUnit (não é um componente Blazor): dublês
/// escritos à mão, mesmo padrão já usado em
/// <see cref="TccManager.Tests.Client.Handlers.AuthTokenHandlerTests"/> (inner handler espião)
/// e <see cref="FakeLocalStorageService"/>.
/// </summary>
public class TokenRefreshCoordinatorTests
{
    /// <summary>
    /// Dublê do <c>authToken</c> gravado por outra aba no <c>localStorage</c> compartilhado
    /// (issue #85, §6.2). O responder é executado durante a chamada HTTP, ou seja,
    /// exatamente no intervalo em que a aba vencedora gravaria o novo token.
    /// </summary>
    private const string AuthTokenDeOutraAba = "auth-gravado-por-outra-aba";

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
    public async Task SemAuthTokenSalvo_EncerraSessaoSemChamarOServidor()
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
        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => new HttpResponseMessage(HttpStatusCode.OK));

        var resultado = await coordinator.EnsureRefreshedAsync("token-antigo-usado-na-chamada-que-falhou");

        Assert.True(resultado);
        Assert.Equal(0, espiao.Chamadas);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task RefreshComSucesso_ChamaARotaCorretaSemCorpo()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        var novaResposta = new RefreshResponseDto { Token = "token-novo" };

        var (coordinator, espiao, sessionEndedHandler, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(novaResposta)
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal(HttpMethod.Post, espiao.UltimaRequisicao!.Method);
        Assert.Equal("/api/auth/refresh", espiao.UltimaRequisicao.RequestUri!.AbsolutePath);
        Assert.Null(espiao.UltimaRequisicao.Content);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task RefreshComSucesso_GravaSoAuthTokenNuncaRefreshToken()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        var novaResposta = new RefreshResponseDto { Token = "token-novo" };

        var (coordinator, _, _, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(novaResposta)
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal("token-novo", storage.Store["authToken"]);
        Assert.False(storage.Store.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task RefreshComSucesso_NotificaOAuthStateProvider()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        var novaResposta = new RefreshResponseDto { Token = "token-novo" };

        var espiao = new HandlerEspiao(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(novaResposta) });
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
    public async Task RespostaDeErroDoServidorComAuthTokenInalterado_EncerraSessao()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal("token-expirado", storage.Store["authToken"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RespostaComTokenAusenteOuVazioEAuthTokenInalterado_EncerraSessao(string? token)
    {
        // Servidor devolveu 200 mas com corpo malformado/incompleto — não pode deixar a
        // sessão num estado inconsistente.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";
        var respostaIncompleta = new RefreshResponseDto { Token = token ?? "" };

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(respostaIncompleta)
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
    }

    // ══════════ Achado L4 da revisão de segurança (#171, A10) — corpo 2xx não-JSON ══════════

    [Fact]
    public async Task RespostaDeSucessoComCorpoNaoJson_NaoLancaExcecaoNaoTratada_EEncerraSessao()
    {
        // Antes da correção, response.Content.ReadFromJsonAsync ficava FORA do try: um 2xx
        // com corpo que não é JSON válido (proxy devolvendo HTML, corpo truncado) lançava
        // JsonException sem tratamento, em vez de cair no mesmo caminho de recuperação que
        // falha de transporte/resposta não-2xx já usam.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, espiao, sessionEndedHandler, _) = Montar(storage, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>proxy indisponível</html>", System.Text.Encoding.UTF8, "application/json")
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
        Assert.Equal("token-expirado", storage.Store["authToken"]);
    }

    [Fact]
    public async Task RespostaDeSucessoComCorpoNaoJsonMasOutraAbaJaRenovou_AdotaOAuthTokenNovoSemEncerrarSessao()
    {
        // Mesmo caminho de recuperação (AdotarSeOutraAbaJaRenovouAsync) que as demais falhas
        // já usam: se outra aba gravou o authToken novo enquanto este corpo malformado
        // chegava, adota em vez de encerrar.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ =>
        {
            storage.Store["authToken"] = AuthTokenDeOutraAba;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("não é json", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
        Assert.Equal(AuthTokenDeOutraAba, storage.Store["authToken"]);
    }

    // ══════════ Issue #85 / §6.2 — falha de transporte e adoção do authToken de outra aba ══════════

    [Fact]
    public async Task FalhaDeRedeNaChamadaDeRefresh_NaoPropagaExcecao_EEncerraSessao()
    {
        // Achado confirmado pelo architect (cenário B3): não havia try/catch em volta do
        // PostAsync, então uma queda de rede escapava como exceção não tratada até a página.
        // Agora precisa ser absorvida e cair no mesmo tratamento das demais falhas.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, espiao, sessionEndedHandler, _) = Montar(
            storage, _ => throw new HttpRequestException("rede indisponível"));

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
        Assert.Equal("token-expirado", storage.Store["authToken"]);
    }

    [Fact]
    public async Task FalhaDeRedeMasOutraAbaJaRenovou_AdotaOAuthTokenNovoSemEncerrarSessao()
    {
        // Rede de segurança de §6.2: a exceção acontece depois de o servidor já ter
        // rotacionado (a resposta é que se perdeu). Se outra aba gravou o authToken novo no
        // localStorage compartilhado nesse meio-tempo, encerrar a sessão derrubaria as duas
        // abas por nada.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ =>
        {
            storage.Store["authToken"] = AuthTokenDeOutraAba;
            throw new HttpRequestException("rede indisponível");
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
        Assert.Equal(AuthTokenDeOutraAba, storage.Store["authToken"]);
    }

    [Fact]
    public async Task RespostaDeErroMasOutraAbaJaRenovou_AdotaOAuthTokenNovoSemEncerrarSessao()
    {
        // Mesmo caminho para o 401 "benigno" que sobra quando a janela de graça do servidor não
        // cobre o caso (ex.: cache miss, D6): o cliente ainda consegue se salvar relendo o
        // storage compartilhado.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ =>
        {
            storage.Store["authToken"] = AuthTokenDeOutraAba;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task PayloadMalformadoMasOutraAbaJaRenovou_AdotaOAuthTokenNovoSemEncerrarSessao()
    {
        // Terceiro caminho de falha que passa pela mesma releitura: 200 com corpo sem Token.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, _, sessionEndedHandler, _) = Montar(storage, _ =>
        {
            storage.Store["authToken"] = AuthTokenDeOutraAba;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RefreshResponseDto { Token = "" })
            };
        });

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.Equal(0, sessionEndedHandler.Chamadas);
    }

    [Fact]
    public async Task AdocaoDoAuthTokenDeOutraAba_NotificaOAuthStateProvider()
    {
        // Sem a notificação, a UI continuaria mostrando o estado anônimo/expirado mesmo com um
        // authToken válido em storage — a sessão estaria viva, mas a página não saberia.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var espiao = new HandlerEspiao(_ =>
        {
            storage.Store["authToken"] = AuthTokenDeOutraAba;
            throw new HttpRequestException("rede indisponível");
        });
        var factory = new FakeHttpClientFactory(new HttpClient(espiao) { BaseAddress = new Uri("https://localhost:7188/") });
        var sessionEndedHandler = new FakeSessionEndedHandler();
        var authStateProvider = NovoAuthStateProvider(storage);

        AuthenticationState? estadoNotificado = null;
        authStateProvider.AuthenticationStateChanged += task => estadoNotificado = task.Result;

        var coordinator = new TokenRefreshCoordinator(factory, storage, sessionEndedHandler, authStateProvider);

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.True(resultado);
        Assert.NotNull(estadoNotificado);
    }

    [Fact]
    public async Task FalhaDeRedeComAuthTokenInalteradoNoStorage_EncerraSessao()
    {
        // Contraprova das adoções acima: se a releitura devolve o MESMO authToken que acabou
        // de ser usado na chamada que falhou, não houve outra aba — a sessão precisa ser
        // encerrada de fato. §6.2 é rede de segurança, não uma forma de nunca deslogar.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-expirado";

        var (coordinator, _, sessionEndedHandler, _) = Montar(
            storage, _ => throw new TaskCanceledException("timeout de transporte"));

        var resultado = await coordinator.EnsureRefreshedAsync("token-expirado");

        Assert.False(resultado);
        Assert.Equal(1, sessionEndedHandler.Chamadas);
    }
}
