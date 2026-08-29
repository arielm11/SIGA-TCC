using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Pages.Coordenador;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Client.Pages.Coordenador;

/// <summary>
/// Issue #75 — primeiro teste bUnit real do projeto (componente Blazor efetivamente
/// renderizado, não reflection sobre <c>new Componente()</c>). Escolhido como ponto de
/// partida por ser o mais simples dos 4 componentes citados na issue: sem DialogService, sem
/// IJSRuntime, sem componente filho aberto como modal.
/// </summary>
public class DashboardTests : BunitContext
{
    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpResponseMessage> Resposta)> _respostas = new();

        public List<string> Chamadas { get; } = new();

        public HandlerMultiRota ComRota(string prefixo, Func<HttpResponseMessage> resposta)
        {
            _respostas.Add((prefixo, resposta));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var caminho = request.RequestUri!.PathAndQuery;
            Chamadas.Add(caminho);

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (caminho.StartsWith(prefixo, StringComparison.Ordinal))
                    return Task.FromResult(resposta());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    public DashboardTests()
    {
        // Radzen dispara chamadas de JS interop internas (medição de layout, etc.) que não
        // são o objeto deste teste — Loose evita falha por chamada não configurada.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
    }

    private HandlerMultiRota RegistrarHttpComDadosPadrao()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/dashboard-stats", () => Json(new DashboardCoordenadorDto
            {
                TotalAtivos = 12,
                PropostasPendentes = 1,
                AguardandoBanca = 3,
                TccsConcluidos = 7
            }))
            .ComRota("/api/coordenador/propostas-pendentes", () => Json(new List<TccResumoDto>
            {
                new() { Id = 1, Titulo = "Proposta de Teste", NomeAluno = "Aluno Um", DataCriacao = DateTime.UtcNow }
            }))
            .ComRota("/api/coordenador/professores", () => Json(new PagedResult<ProfessorResumoDto>
            {
                Items = new List<ProfessorResumoDto>
                {
                    new() { Id = 10, Nome = "Prof. Teste", CargaAtual = 2, LimiteOrientandos = 5, AceitandoOrientandos = true }
                },
                TotalCount = 1,
                TotalPages = 1,
                CurrentPage = 1,
                PageSize = 100
            }));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

    [Fact]
    public void Renderiza_CarregaAsEstatisticasViaHttpEExibeOsCards()
    {
        var handler = RegistrarHttpComDadosPadrao();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("TCCs Ativos", cut.Markup));

        Assert.Contains("12", cut.Markup);
        Assert.Contains("Aguardando Banca", cut.Markup);
        Assert.Contains(handler.Chamadas, c => c.StartsWith("/api/coordenador/dashboard-stats"));
    }

    [Fact]
    public void Renderiza_ExibePropostaPendenteNaGrid()
    {
        RegistrarHttpComDadosPadrao();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Um", cut.Markup));
        Assert.Contains("Proposta de Teste", cut.Markup);
    }

    [Fact]
    public void FalhaDeHttp_ExibeNotificacaoDeErro_NaoLancaExcecaoNaoTratada()
    {
        var handler = new HandlerMultiRota(); // nenhuma rota configurada -> 404 em tudo
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
    }

    [Fact]
    public void NomeDoAlunoComMarcacaoHtml_NaoViraElementoScriptNoDom()
    {
        // Issue #75 ("XSS de saída não testada"): Dashboard.razor interpola NomeAluno via
        // @tcc.NomeAluno (texto puro) — não usa MarkupString em nenhum ponto. Isso é seguro
        // independente de a serialização final escapar "<"/">" (dentro de um atributo HTML
        // como o "title" de tooltip do Radzen, "<"/">" são literais inofensivos — só "&" e a
        // aspas delimitadora importam ali; navegador nunca reparsa o valor de um atributo
        // como HTML). O que de fato importa para segurança é não existir um elemento
        // &lt;script&gt; de verdade no DOM resultante — é isso que este teste prova, e é o
        // guard de regressão contra uma futura troca dessa interpolação por MarkupString
        // sobre dado não confiável.
        var payload = "<script>alert(1)</script>";
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/dashboard-stats", () => Json(new DashboardCoordenadorDto()))
            .ComRota("/api/coordenador/propostas-pendentes", () => Json(new List<TccResumoDto>
            {
                new() { Id = 1, Titulo = "Proposta", NomeAluno = payload, DataCriacao = DateTime.UtcNow }
            }))
            .ComRota("/api/coordenador/professores", () => Json(new PagedResult<ProfessorResumoDto>
            {
                Items = new List<ProfessorResumoDto>(), TotalCount = 0, TotalPages = 0, CurrentPage = 1, PageSize = 100
            }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Proposta", cut.Markup));
        Assert.Empty(cut.FindAll("script"));
    }

    [Fact]
    public void AbrirDesignacao_ClicarDesignar_ExibeCardDeSelecaoDeOrientador()
    {
        RegistrarHttpComDadosPadrao();
        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        var botaoDesignar = cut.FindAll("button").Single(b => b.TextContent.Contains("Designar"));
        botaoDesignar.Click();

        cut.WaitForAssertion(() => Assert.Contains("Designar Orientador para: Aluno Um", cut.Markup));
    }
}
