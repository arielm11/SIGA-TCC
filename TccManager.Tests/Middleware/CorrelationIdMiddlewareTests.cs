using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Middleware;

/// <summary>
/// Testes de integração do <c>CorrelationIdMiddleware</c> (S1 / RF1.3 / RF1.4).
/// O header usado é "X-Correlation-Id".
/// </summary>
public class CorrelationIdMiddlewareTests
{
    private const string HeaderName = "X-Correlation-Id";
    private const string RotaLogin = "/api/auth/login";

    private static LoginDto Credencial() =>
        new() { Email = "qualquer@teste.com", Senha = "x" };

    [Fact]
    public async Task Resposta_SemHeaderNaRequisicao_DeveConterCorrelationIdGerado()
    {
        // Arrange
        var factory = new TccApiFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(RotaLogin, Credencial());

        // Assert
        Assert.True(response.Headers.Contains(HeaderName),
            $"A resposta deve conter o header {HeaderName}.");

        var valor = response.Headers.GetValues(HeaderName).First();
        Assert.False(string.IsNullOrWhiteSpace(valor));
        Assert.True(Guid.TryParse(valor, out _),
            $"Na ausência de header enviado pelo cliente, o {HeaderName} deve ser um GUID gerado. Valor: '{valor}'.");
    }

    [Fact]
    public async Task Resposta_ComHeaderGuidValidoEnviadoPeloCliente_DevePropagarOMesmoValor()
    {
        // Arrange
        var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var correlationIdCliente = Guid.NewGuid().ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, RotaLogin)
        {
            Content = JsonContent.Create(Credencial())
        };
        request.Headers.Add(HeaderName, correlationIdCliente);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.Headers.Contains(HeaderName));
        var valor = response.Headers.GetValues(HeaderName).First();
        Assert.Equal(correlationIdCliente, valor);
    }

    [Fact]
    public async Task Resposta_ComHeaderInvalidoEnviadoPeloCliente_DeveGerarNovoGuid()
    {
        // Arrange: um atacante não deve conseguir forjar um CorrelationId arbitrário (não-GUID) na trilha de auditoria.
        var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var correlationIdForjado = "correlacao-fornecida-pelo-cliente-123";

        var request = new HttpRequestMessage(HttpMethod.Post, RotaLogin)
        {
            Content = JsonContent.Create(Credencial())
        };
        request.Headers.Add(HeaderName, correlationIdForjado);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True(response.Headers.Contains(HeaderName));
        var valor = response.Headers.GetValues(HeaderName).First();
        Assert.NotEqual(correlationIdForjado, valor);
        Assert.True(Guid.TryParse(valor, out _),
            $"Um valor de header não-GUID enviado pelo cliente deve ser substituído por um GUID gerado. Valor: '{valor}'.");
    }
}
