using System.Net;
using System.Net.Http;
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Layout;
using TccManager.Client.Providers;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Layout;

/// <summary>
/// Issue #171 — achado L2 da revisão de segurança (A07): o <c>Logout()</c> de <see cref="NavMenu"/>
/// ignorava o status HTTP de <c>/api/auth/logout</c>, limpando o estado local e navegando para
/// <c>/login</c> mesmo quando o servidor não confirmou. Como o refresh token agora vive num cookie
/// <c>HttpOnly</c> (<c>siga_refresh</c>), só o servidor pode expirá-lo (via <c>Set-Cookie</c>) — e
/// isso só acontece se <c>/api/auth/logout</c> respondeu de verdade.
///
/// Sem infraestrutura dedicada para renderizar <c>RadzenProfileMenu</c>/<c>AuthorizeView</c> com um
/// JWT de teste (o <c>Logout()</c> não depende de nada disso), os testes seguem o mesmo padrão já
/// usado em <c>DetalhesTccTests</c>: <c>new NavMenu()</c> + injeção dos membros <c>@inject</c> via
/// reflection (todos <c>protected</c> no código gerado pelo compilador Razor) + invocação direta do
/// método privado. <see cref="NavigationManager"/> vem do <see cref="BunitContext"/> (mesmo truque de
/// <c>SessionEndedHandlerTests</c>) só como dublê — nenhum componente é renderizado.
/// </summary>
public class NavMenuTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed class HandlerDeSequencia : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _respostas;

        public HandlerDeSequencia(params Func<HttpResponseMessage>[] respostas) =>
            _respostas = new Queue<Func<HttpResponseMessage>>(respostas);

        public int Chamadas { get; private set; }
        public List<string> Caminhos { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas++;
            Caminhos.Add(request.RequestUri!.AbsolutePath);

            var proximaResposta = _respostas.Count > 0
                ? _respostas.Dequeue()
                : () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return Task.FromResult(proximaResposta());
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static NavMenu NovoComponente(
        FakeLocalStorageService storage,
        HandlerDeSequencia espiao,
        NotificationService notificationService,
        out NavigationManager navigationManager)
    {
        var componente = new NavMenu();

        var authStateProvider = new CustomAuthStateProvider(storage, new ServiceCollection().BuildServiceProvider());
        var httpClientFactory = new FakeHttpClientFactory(new HttpClient(espiao) { BaseAddress = new Uri("https://localhost:7146/") });

        // Dublê de NavigationManager, sem renderizar nenhum componente — mesmo truque de
        // SessionEndedHandlerTests.
        var ctx = new BunitContext();
        navigationManager = ctx.Services.GetRequiredService<NavigationManager>();

        DefinirInjetado(componente, "AuthStateProvider", authStateProvider);
        DefinirInjetado(componente, "LocalStorage", storage);
        DefinirInjetado(componente, "Navigation", navigationManager);
        DefinirInjetado(componente, "HttpClientFactory", httpClientFactory);
        DefinirInjetado(componente, "NotificationService", notificationService);

        return componente;
    }

    private static void DefinirInjetado(NavMenu componente, string nome, object valor)
    {
        var propriedade = typeof(NavMenu).GetProperty(nome, Privados)
            ?? throw new MissingMemberException(nameof(NavMenu), nome);
        propriedade.SetValue(componente, valor);
    }

    private static async Task InvocarLogoutAsync(NavMenu componente)
    {
        var metodo = typeof(NavMenu).GetMethod("Logout", Privados)
            ?? throw new MissingMethodException(nameof(NavMenu), "Logout");
        var tarefa = (Task)metodo.Invoke(componente, null)!;
        await tarefa;
    }

    [Fact]
    public async Task LogoutComSucessoNaPrimeiraTentativa_LimpaEstadoLocalENavegaParaLogin_SemNotificarErro()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        storage.Store["refreshToken"] = "valor-legado";
        var espiao = new HandlerDeSequencia(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        var notificationService = new NotificationService();

        var componente = NovoComponente(storage, espiao, notificationService, out var navigationManager);

        await InvocarLogoutAsync(componente);

        Assert.Equal(1, espiao.Chamadas);
        Assert.Equal("/api/auth/logout", espiao.Caminhos.Single());
        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.False(storage.Store.ContainsKey("refreshToken"));
        Assert.EndsWith("/login", navigationManager.Uri);
        Assert.Empty(notificationService.Messages);
    }

    [Fact]
    public async Task LogoutFalhaNaPrimeiraMasConfirmaNaSegundaTentativa_NaoNotificaErro()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        var espiao = new HandlerDeSequencia(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => new HttpResponseMessage(HttpStatusCode.NoContent));
        var notificationService = new NotificationService();

        var componente = NovoComponente(storage, espiao, notificationService, out var navigationManager);

        await InvocarLogoutAsync(componente);

        Assert.Equal(2, espiao.Chamadas);
        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.EndsWith("/login", navigationManager.Uri);
        Assert.Empty(notificationService.Messages);
    }

    [Fact]
    public async Task LogoutFalhaEmTodasAsTentativas_AindaLimpaEstadoLocalENavegaParaLogin()
    {
        // O usuário não pode ficar presa numa tela de erro: a UI local já não tem mais sessão
        // ativa daqui para frente, mesmo sem confirmação do servidor.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        storage.Store["refreshToken"] = "valor-legado";
        var espiao = new HandlerDeSequencia(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var notificationService = new NotificationService();

        var componente = NovoComponente(storage, espiao, notificationService, out var navigationManager);

        await InvocarLogoutAsync(componente);

        Assert.Equal(3, espiao.Chamadas);
        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.False(storage.Store.ContainsKey("refreshToken"));
        Assert.EndsWith("/login", navigationManager.Uri);
    }

    [Fact]
    public async Task LogoutFalhaEmTodasAsTentativas_NotificaErroComMensagemDeComputadorCompartilhado()
    {
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        var espiao = new HandlerDeSequencia(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var notificationService = new NotificationService();

        var componente = NovoComponente(storage, espiao, notificationService, out _);

        await InvocarLogoutAsync(componente);

        var mensagem = Assert.Single(notificationService.Messages);
        Assert.Equal(NotificationSeverity.Error, mensagem.Severity);
        Assert.Contains("computador compartilhado", mensagem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feche completamente o navegador", mensagem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogoutComFalhaDeRedeEmTodasAsTentativas_NaoPropagaExcecao_LimpaEstadoENotifica()
    {
        // Exceção de transporte (não apenas status não-2xx) em todas as tentativas — mesma
        // rede de segurança best-effort, nunca deve escapar como exceção não tratada.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = "token-atual";
        var espiao = new HandlerDeSequencia(
            () => throw new HttpRequestException("rede indisponível"),
            () => throw new HttpRequestException("rede indisponível"),
            () => throw new HttpRequestException("rede indisponível"));
        var notificationService = new NotificationService();

        var componente = NovoComponente(storage, espiao, notificationService, out var navigationManager);

        await InvocarLogoutAsync(componente);

        Assert.False(storage.Store.ContainsKey("authToken"));
        Assert.EndsWith("/login", navigationManager.Uri);
        Assert.Single(notificationService.Messages);
    }
}
