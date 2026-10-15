using System.Net;
using System.Net.Http.Headers;
using TccManager.Client.Handlers;
using TccManager.Client.Services;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Handlers;

/// <summary>
/// Issue #63 — o <see cref="AuthTokenHandler"/> anexava o header
/// <c>Authorization: Bearer</c> a QUALQUER requisição que passasse por ele, inclusive as de
/// URI absoluta apontando para um host externo. Como o handler é registrado no HttpClient
/// nomeado "Api", bastaria um uso indevido (ou um bug futuro) passando uma URL de terceiro
/// para vazar o token de sessão do usuário para fora.
///
/// Agora o bearer só é anexado quando Scheme+Host+Port do destino batem com a URI base da
/// API injetada no handler, ou quando a URI é relativa (resolvida pelo BaseAddress).
///
/// Infraestrutura: os testes usam o <see cref="FakeLocalStorageService"/> já existente no
/// projeto e um par de dublês locais (inner handler espião + coordenador de refresh), no
/// mesmo padrão de fakes escritos à mão adotado no repositório — sem introduzir Moq/NSubstitute.
///
/// Observação sobre "requisição relativa": o cenário é exercitado via <see cref="HttpClient"/>
/// com <c>BaseAddress</c> definido, que é como ele ocorre em produção. Invocar o handler
/// diretamente com uma <c>RequestUri</c> relativa (por HttpMessageInvoker) não é um cenário
/// real e esbarraria em <c>Uri.AbsolutePath</c> sobre URI relativa dentro de
/// <c>EhRotaAuth</c> — comportamento preexistente à issue #63, registrado no relatório.
/// </summary>
public class AuthTokenHandlerTests
{
    private const string ApiBaseUrl = "https://localhost:7188/";
    private const string TokenSalvo = "token-de-sessao-abc";

    /// <summary>Inner handler espião: registra o header Authorization no momento do envio.</summary>
    private sealed class HandlerEspiao : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statusPlanejados;

        public HandlerEspiao(params HttpStatusCode[] statusPlanejados)
        {
            _statusPlanejados = new Queue<HttpStatusCode>(
                statusPlanejados.Length > 0 ? statusPlanejados : new[] { HttpStatusCode.OK });
        }

        public List<Uri?> Destinos { get; } = new();

        /// <summary>
        /// Capturado no envio, não depois: o HttpClient pode dispor a HttpRequestMessage
        /// assim que a chamada termina.
        /// </summary>
        public List<AuthenticationHeaderValue?> Autorizacoes { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Destinos.Add(request.RequestUri);
            Autorizacoes.Add(request.Headers.Authorization);

            var status = _statusPlanejados.Count > 1
                ? _statusPlanejados.Dequeue()
                : _statusPlanejados.Peek();

            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class FakeTokenRefreshCoordinator : ITokenRefreshCoordinator
    {
        private readonly FakeLocalStorageService _storage;
        private readonly bool _sucesso;
        private readonly string? _novoToken;

        public FakeTokenRefreshCoordinator(
            FakeLocalStorageService storage, bool sucesso = false, string? novoToken = null)
        {
            _storage = storage;
            _sucesso = sucesso;
            _novoToken = novoToken;
        }

        public int Chamadas { get; private set; }

        public Task<bool> EnsureRefreshedAsync(string tokenUsado)
        {
            Chamadas++;

            if (_sucesso && _novoToken != null)
                _storage.Store["authToken"] = $"\"{_novoToken}\"";

            return Task.FromResult(_sucesso);
        }
    }

    private static (HttpClient Client, HandlerEspiao Espiao, FakeLocalStorageService Storage)
        Montar(string? token = TokenSalvo, params HttpStatusCode[] statusPlanejados)
    {
        var storage = new FakeLocalStorageService();
        if (token != null)
        {
            // O Blazored grava strings entre aspas; o handler as remove ao ler.
            storage.Store["authToken"] = $"\"{token}\"";
        }

        var espiao = new HandlerEspiao(statusPlanejados);

        var handler = new AuthTokenHandler(storage, new FakeTokenRefreshCoordinator(storage), new Uri(ApiBaseUrl))
        {
            InnerHandler = espiao
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };

        return (client, espiao, storage);
    }

    // ─────────────── Destinos que DEVEM receber o bearer ───────────────

    [Fact]
    public async Task RequisicaoRelativa_AnexaBearer()
    {
        var (client, espiao, _) = Montar();

        await client.GetAsync("api/tcc");

        var autorizacao = Assert.Single(espiao.Autorizacoes);
        Assert.NotNull(autorizacao);
        Assert.Equal("Bearer", autorizacao!.Scheme);
        Assert.Equal(TokenSalvo, autorizacao.Parameter);
    }

    [Fact]
    public async Task RequisicaoAbsolutaParaAMesmaApi_AnexaBearer()
    {
        var (client, espiao, _) = Montar();

        await client.GetAsync($"{ApiBaseUrl}api/tcc");

        var autorizacao = Assert.Single(espiao.Autorizacoes);
        Assert.NotNull(autorizacao);
        Assert.Equal(TokenSalvo, autorizacao!.Parameter);
    }

    [Fact]
    public async Task RequisicaoAbsolutaComHostEmCaixaDiferente_AnexaBearer()
    {
        // A comparação de host é case-insensitive (OrdinalIgnoreCase), como manda o DNS —
        // não pode "proteger demais" e quebrar a própria API.
        var (client, espiao, _) = Montar();

        await client.GetAsync("https://LOCALHOST:7188/api/tcc");

        Assert.NotNull(Assert.Single(espiao.Autorizacoes));
    }

    // ─────────────── Destinos que NÃO podem receber o bearer ───────────────

    [Fact]
    public async Task RequisicaoParaHostExterno_NaoAnexaBearer()
    {
        // Núcleo da issue #63.
        var (client, espiao, _) = Montar();

        await client.GetAsync("https://api.terceiro-malicioso.com/coleta");

        Assert.Null(Assert.Single(espiao.Autorizacoes));
    }

    [Fact]
    public async Task RequisicaoParaSubdominioDaApi_NaoAnexaBearer()
    {
        var (client, espiao, _) = Montar();

        await client.GetAsync("https://evil.localhost:7188/api/tcc");

        Assert.Null(Assert.Single(espiao.Autorizacoes));
    }

    [Fact]
    public async Task RequisicaoParaMesmoHostEmPortaDiferente_NaoAnexaBearer()
    {
        // Porta faz parte da origem: outro processo escutando em outra porta da mesma
        // máquina não é a API configurada.
        var (client, espiao, _) = Montar();

        await client.GetAsync("https://localhost:9999/api/tcc");

        Assert.Null(Assert.Single(espiao.Autorizacoes));
    }

    [Fact]
    public async Task RequisicaoParaMesmoHostEPortaEmSchemeDiferente_NaoAnexaBearer()
    {
        var (client, espiao, _) = Montar();

        await client.GetAsync("http://localhost:7188/api/tcc");

        Assert.Null(Assert.Single(espiao.Autorizacoes));
    }

    [Fact]
    public async Task SemTokenSalvo_NaoAnexaBearerNemParaAApi()
    {
        var (client, espiao, _) = Montar(token: null);

        await client.GetAsync("api/tcc");

        Assert.Null(Assert.Single(espiao.Autorizacoes));
    }

    // ─────────────── Retry pós-refresh também respeita o destino ───────────────

    [Fact]
    public async Task Retry_AposRefreshBemSucedido_AnexaONovoTokenQuandoODestinoEhAApi()
    {
        const string novoToken = "token-renovado-xyz";

        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = $"\"{TokenSalvo}\"";

        var espiao = new HandlerEspiao(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var coordenador = new FakeTokenRefreshCoordinator(storage, sucesso: true, novoToken: novoToken);

        var handler = new AuthTokenHandler(storage, coordenador, new Uri(ApiBaseUrl))
        {
            InnerHandler = espiao
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };

        var resposta = await client.GetAsync("api/tcc");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(1, coordenador.Chamadas);
        Assert.Equal(2, espiao.Autorizacoes.Count);
        Assert.Equal(TokenSalvo, espiao.Autorizacoes[0]!.Parameter);
        Assert.Equal(novoToken, espiao.Autorizacoes[1]!.Parameter);
    }

    [Fact]
    public async Task Retry_AposRefreshBemSucedido_NaoAnexaTokenQuandoODestinoEhExterno()
    {
        // O segundo ponto onde o bearer é anexado (a requisição clonada do retry) precisa
        // aplicar a mesma checagem de destino do primeiro.
        const string novoToken = "token-renovado-xyz";

        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = $"\"{TokenSalvo}\"";

        var espiao = new HandlerEspiao(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var coordenador = new FakeTokenRefreshCoordinator(storage, sucesso: true, novoToken: novoToken);

        var handler = new AuthTokenHandler(storage, coordenador, new Uri(ApiBaseUrl))
        {
            InnerHandler = espiao
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };

        await client.GetAsync("https://api.terceiro-malicioso.com/coleta");

        Assert.Equal(2, espiao.Autorizacoes.Count);
        Assert.All(espiao.Autorizacoes, Assert.Null);
    }

    // ─────────────── Comportamento preexistente que não pode regredir ───────────────

    [Fact]
    public async Task RotaDeAuth_NaoDisparaRefreshMesmoCom401()
    {
        // Bypass das rotas de auth (§6.1 da arquitetura): um 401 de credencial inválida
        // não pode acionar o coordenador de refresh.
        var storage = new FakeLocalStorageService();
        storage.Store["authToken"] = $"\"{TokenSalvo}\"";

        var espiao = new HandlerEspiao(HttpStatusCode.Unauthorized);
        var coordenador = new FakeTokenRefreshCoordinator(storage, sucesso: true, novoToken: "nao-deveria-usar");

        var handler = new AuthTokenHandler(storage, coordenador, new Uri(ApiBaseUrl))
        {
            InnerHandler = espiao
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };

        var resposta = await client.PostAsync("api/auth/login", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(0, coordenador.Chamadas);
        Assert.Single(espiao.Autorizacoes);
    }
}
