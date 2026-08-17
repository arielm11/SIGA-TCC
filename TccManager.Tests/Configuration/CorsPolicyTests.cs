using System.Net;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #64 — a lista de origens da política CORS "AllowBlazorClient" deixou de ser
/// hardcoded no <c>Program.cs</c> e passou a vir de <c>Cors:AllowedOrigins</c>
/// (appsettings.json), com fallback para as duas origens de desenvolvimento
/// (<c>https://localhost:7249</c> e <c>http://localhost:5075</c>) quando a seção não existe.
///
/// A rota usada nas asserções (<c>/api/usuario/me</c>) é irrelevante para o resultado: o
/// <c>UseCors</c> está registrado no pipeline global, antes de autenticação/autorização, então
/// os cabeçalhos de CORS são escritos mesmo em resposta 401. Isso mantém os testes focados na
/// política e independentes de dados no banco.
///
/// Isolamento: cada teste sobe sua própria factory (host próprio, configuração própria).
/// </summary>
public class CorsPolicyTests
{
    private const string Rota = "/api/usuario/me";
    private const string HeaderAllowOrigin = "Access-Control-Allow-Origin";

    private const string OrigemDevHttps = "https://localhost:7249";
    private const string OrigemDevHttp = "http://localhost:5075";

    private static HttpRequestMessage RequisicaoSimples(string origem)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Get, Rota);
        requisicao.Headers.Add("Origin", origem);
        return requisicao;
    }

    private static HttpRequestMessage Preflight(string origem, string metodo = "POST", string? headers = null)
    {
        var requisicao = new HttpRequestMessage(HttpMethod.Options, Rota);
        requisicao.Headers.Add("Origin", origem);
        requisicao.Headers.Add("Access-Control-Request-Method", metodo);

        if (headers is not null)
            requisicao.Headers.Add("Access-Control-Request-Headers", headers);

        return requisicao;
    }

    private static string? AllowOrigin(HttpResponseMessage resposta) =>
        resposta.Headers.TryGetValues(HeaderAllowOrigin, out var valores)
            ? valores.FirstOrDefault()
            : null;

    // ── Origens do fallback de desenvolvimento (Program.cs) ───────────────────────────────
    // O appsettings.json versionado NÃO fixa mais essas duas origens ("Cors": {} — ver
    // OrigemDefinidaEmConfiguracao_PassaASerAceita, mais abaixo, para o motivo). A TccApiFactory
    // usada aqui não sobrescreve "Cors", então quem responde por essas duas origens aceitas é o
    // fallback hardcoded em Program.cs, não uma seção do appsettings.json.

    [Theory]
    [InlineData(OrigemDevHttps)]
    [InlineData(OrigemDevHttp)]
    public async Task OrigemDoFallbackDeDesenvolvimento_RequisicaoSimples_RecebeAllowOrigin(string origem)
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.SendAsync(RequisicaoSimples(origem));

        // Sanidade: a requisição chegou ao pipeline da API (não virou redirect de HTTPS).
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(origem, AllowOrigin(resposta));
    }

    [Theory]
    [InlineData("https://malicioso.example.com")]
    [InlineData("http://localhost:7249")]   // mesma porta, esquema diferente
    [InlineData("https://localhost:7250")]  // mesmo esquema, porta diferente
    [InlineData("https://localhost")]       // sem porta
    public async Task OrigemForaDaConfiguracao_RequisicaoSimples_NaoRecebeAllowOrigin(string origem)
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.SendAsync(RequisicaoSimples(origem));

        Assert.Null(AllowOrigin(resposta));
    }

    [Fact]
    public async Task Preflight_OrigemDeclarada_Retorna204ComMetodoECabecalhosLiberados()
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.SendAsync(Preflight(OrigemDevHttps, "POST", "content-type"));

        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);
        Assert.Equal(OrigemDevHttps, AllowOrigin(resposta));

        // A política usa AllowAnyMethod/AllowAnyHeader: o preflight ecoa o que foi pedido.
        Assert.Contains("POST", resposta.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Contains("content-type", resposta.Headers.GetValues("Access-Control-Allow-Headers"));
    }

    [Fact]
    public async Task Preflight_OrigemForaDaConfiguracao_NaoRecebeCabecalhosDeCors()
    {
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.SendAsync(Preflight("https://malicioso.example.com"));

        Assert.Null(AllowOrigin(resposta));
        Assert.False(resposta.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task PoliticaNaoLiberaCredenciais()
    {
        // Guarda de regressão: a política nunca teve AllowCredentials. Combinado com
        // origens explícitas, é o que impede um site terceiro de reaproveitar sessão.
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.SendAsync(RequisicaoSimples(OrigemDevHttps));

        Assert.False(resposta.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    // ── Origens vindas de configuração customizada ────────────────────────────────────────

    [Fact]
    public async Task OrigemDefinidaEmConfiguracao_PassaASerAceita()
    {
        // Núcleo da correção: trocar Cors:AllowedOrigins:0 muda a lista efetiva do host.
        // "https://localhost:7249" deixa de ser aceito — prova de que a lista vem da
        // configuração, e não de constantes no código.
        //
        // O appsettings.json versionado não fixa mais os dois valores de dev na seção "Cors"
        // (fica só {}) — achado do security-reviewer (2026-08-17): arrays de configuração no
        // .NET fazem merge por índice entre camadas, então valores fixados aqui vazariam para
        // qualquer override parcial em appsettings.Production.json/env vars. Por isso, ao
        // sobrescrever só o índice 0, o índice 1 (localhost:5075) também deixa de existir na
        // lista efetiva — não há mais um valor base ali para "sobrar".
        const string origemCustomizada = "https://tcc.exemplo.edu.br";

        using var factory = new ConfiguracaoCustomizadaApiFactory(
            new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = origemCustomizada });

        var client = factory.CreateClient();

        var aceita = await client.SendAsync(RequisicaoSimples(origemCustomizada));
        Assert.Equal(origemCustomizada, AllowOrigin(aceita));

        var substituida = await client.SendAsync(RequisicaoSimples(OrigemDevHttps));
        Assert.Null(AllowOrigin(substituida));

        // Sem valor base versionado no índice 1, ele também deixa de ser aceito — nada "sobra"
        // de dev para vazar quando só uma origem é configurada.
        var semValorBase = await client.SendAsync(RequisicaoSimples(OrigemDevHttp));
        Assert.Null(AllowOrigin(semValorBase));
    }

    [Fact]
    public async Task SecaoCorsAusente_CaiNoFallbackDeDesenvolvimento()
    {
        // Sem a seção Cors na configuração, o Program.cs precisa cair no fallback
        // ["https://localhost:7249", "http://localhost:5075"]. Se o fallback não existisse,
        // WithOrigins receberia null e o host sequer subiria.
        using var factory = new ConfiguracaoCustomizadaApiFactory(prefixoRemovido: "Cors:");
        var client = factory.CreateClient();

        var https = await client.SendAsync(RequisicaoSimples(OrigemDevHttps));
        Assert.Equal(OrigemDevHttps, AllowOrigin(https));

        var http = await client.SendAsync(RequisicaoSimples(OrigemDevHttp));
        Assert.Equal(OrigemDevHttp, AllowOrigin(http));

        // O fallback é uma lista fechada, não um "libera geral".
        var forasteira = await client.SendAsync(RequisicaoSimples("https://malicioso.example.com"));
        Assert.Null(AllowOrigin(forasteira));
    }

    [Fact]
    public async Task SemHeaderOrigin_RespostaNaoTrazCabecalhosDeCors()
    {
        // Chamadas servidor-a-servidor (sem Origin) não devem ganhar cabeçalhos de CORS.
        using var factory = new TccApiFactory();
        var client = factory.CreateClient();

        var resposta = await client.GetAsync(Rota);

        Assert.Null(AllowOrigin(resposta));
    }
}
