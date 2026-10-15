using System.Net;
using Radzen;
using TccManager.Client.Handlers;
using Xunit;

namespace TccManager.Tests.Client.Handlers;

/// <summary>
/// Issue #107 (achado F-10): <see cref="RateLimitHandler"/> intercepta 429 e mostra uma
/// notificação amigável usando o Retry-After — sem ele, o usuário via só o erro genérico da
/// tela ao esbarrar no rate limiting introduzido pela #74.
/// </summary>
public class RateLimitHandlerTests
{
    private sealed class HandlerFalso : DelegatingHandler
    {
        private readonly HttpResponseMessage _resposta;
        public HandlerFalso(HttpResponseMessage resposta) => _resposta = resposta;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_resposta);
    }

    private static HttpClient CriarCliente(HttpResponseMessage respostaSimulada, NotificationService notificationService)
    {
        var rateLimitHandler = new RateLimitHandler(notificationService)
        {
            InnerHandler = new HandlerFalso(respostaSimulada)
        };
        return new HttpClient(rateLimitHandler) { BaseAddress = new Uri("https://teste.local") };
    }

    [Fact]
    public async Task Resposta429ComRetryAfter_NotificaComSegundosNaMensagem()
    {
        var resposta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resposta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
        var notificationService = new NotificationService();
        var client = CriarCliente(resposta, notificationService);

        await client.GetAsync("/api/coordenador/professores");

        var mensagem = Assert.Single(notificationService.Messages);
        Assert.Equal(NotificationSeverity.Warning, mensagem.Severity);
        Assert.Contains("42 segundos", mensagem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resposta429SemRetryAfter_NotificaComMensagemGenerica()
    {
        var resposta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var notificationService = new NotificationService();
        var client = CriarCliente(resposta, notificationService);

        await client.GetAsync("/api/coordenador/professores");

        var mensagem = Assert.Single(notificationService.Messages);
        Assert.Contains("instantes", mensagem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespostaComSucesso_NaoNotificaNada()
    {
        var resposta = new HttpResponseMessage(HttpStatusCode.OK);
        var notificationService = new NotificationService();
        var client = CriarCliente(resposta, notificationService);

        await client.GetAsync("/api/coordenador/professores");

        Assert.Empty(notificationService.Messages);
    }
}
