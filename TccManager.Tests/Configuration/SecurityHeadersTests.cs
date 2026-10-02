using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #148 (achado B9): X-Content-Type-Options: nosniff precisa estar presente em toda
/// resposta da API (JSON e downloads), como defesa em profundidade contra MIME sniffing.
/// UseHsts não é testável aqui: só é registrado fora de Development
/// (!app.Environment.IsDevelopment()), e o TestServer usado pela suíte roda sempre em
/// Development — verificado por leitura de código e manualmente via curl contra o Kestrel
/// real.
/// </summary>
public class SecurityHeadersTests
{
    [Fact]
    public async Task QualquerResposta_ContemNoSniff()
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        // Rota anônima e simples só para observar os headers de uma resposta qualquer — o
        // middleware roda antes do roteamento/autorização, então se aplica mesmo a um 401/404.
        var response = await client.GetAsync("/api/tcc/meu-tcc");

        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var valores));
        Assert.Contains("nosniff", valores!);
    }
}
