using System.Net;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Testes de integração da política de rate limiting "rascunho-publico" aplicada ao endpoint
/// público GET /api/rascunho-ata/{token} (Etapa 2, RNF de defesa contra enumeração de token).
///
/// Diferença em relação ao endpoint de login: aqui um token inválido retorna 404 (não 401),
/// então a asserção "dentro do limite" verifica != TooManyRequests. PermitLimit padrão é 20
/// (RateLimiting:RascunhoPublico:PermitLimit no appsettings.json), mais folgado que o login.
/// Cada instância de factory tem seu próprio limitador (serviço por host), isolando os casos.
/// </summary>
public class RateLimitingRascunhoPublicoTests
{
    private const int PermitLimit = 20;

    private static string RotaComTokenInvalido() => $"/api/rascunho-ata/{new string('0', 64)}";

    [Fact]
    public async Task RequisicaoAcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var rota = RotaComTokenInvalido();

        // As PermitLimit primeiras requisições passam pelo limitador (retornam 404, não 429).
        for (var i = 1; i <= PermitLimit; i++)
        {
            var permitida = await client.GetAsync(rota);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        // A requisição seguinte dentro da mesma janela é bloqueada.
        var bloqueada = await client.GetAsync(rota);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
        var retryAfter = bloqueada.Headers.GetValues("Retry-After").First();
        Assert.True(int.TryParse(retryAfter, out var segundos),
            $"Retry-After deveria ser um inteiro em segundos, mas foi '{retryAfter}'.");
        Assert.InRange(segundos, 1, 60);
    }

    [Fact]
    public async Task DentroDoLimite_NenhumaEhBloqueada()
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var rota = RotaComTokenInvalido();

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resp = await client.GetAsync(rota);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }
}
