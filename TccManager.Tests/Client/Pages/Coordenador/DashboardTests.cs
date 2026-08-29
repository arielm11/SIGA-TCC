using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using TccManager.Client.Pages.Coordenador;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Client.Pages.Coordenador;

/// <summary>
/// Issue #75 — primeiro teste bUnit real do projeto (componente Blazor efetivamente
/// renderizado, não reflection sobre <c>new Componente()</c>). Escolhido como ponto de
/// partida por ser o mais simples dos 4 componentes citados na issue.
///
/// Issue #76 (D9/P-05) — a tela deixou de ser "a mais simples": ganhou a coluna "Resumo"
/// (toggle portado da tela do Professor) e o fluxo de rejeição, que abre
/// <c>RejeitarPropostaDialog</c> via <c>DialogService.OpenAsync</c>. Para exercitar esse fluxo
/// por clique — e não por reflection — os testes de rejeição renderizam a página ao lado de um
/// host <c>&lt;RadzenDialog/&gt;</c> real, que é o que faltava nos demais testes de diálogo do
/// projeto (ver o comentário de GestaoProfessoresTests/RegistroResultadoBancaTests).
/// </summary>
public class DashboardTests : BunitContext
{
    private sealed record Requisicao(HttpMethod Metodo, string Caminho, string Corpo);

    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpResponseMessage> Resposta)> _respostas = new();

        public List<string> Chamadas { get; } = new();

        /// <summary>
        /// Issue #76: além do caminho, o fluxo de rejeição precisa provar o VERBO (PUT, não
        /// POST — decisão da seção 7.1 da arquitetura) e o CORPO enviados.
        /// </summary>
        public List<Requisicao> Requisicoes { get; } = new();

        public HandlerMultiRota ComRota(string prefixo, Func<HttpResponseMessage> resposta)
        {
            _respostas.Add((prefixo, resposta));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var caminho = request.RequestUri!.PathAndQuery;
            Chamadas.Add(caminho);

            var corpo = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requisicoes.Add(new Requisicao(request.Method, caminho, corpo));

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (caminho.StartsWith(prefixo, StringComparison.Ordinal))
                    return resposta();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    public DashboardTests()
    {
        // Radzen dispara chamadas de JS interop internas (medição de layout, etc.) que não
        // são o objeto deste teste — Loose evita falha por chamada não configurada.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        // Issue #76 (D9): a página passou a injetar DialogService (abre o RejeitarPropostaDialog).
        Services.AddSingleton<DialogService>();
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
                new()
                {
                    Id = 1,
                    Titulo = "Proposta de Teste",
                    // Issue #76 (P-05): GetPropostasPendentes passou a projetar Resumo, para o
                    // Coordenador conseguir ler a proposta antes de designar ou rejeitar.
                    Resumo = "Resumo completo da proposta submetida.",
                    NomeAluno = "Aluno Um",
                    DataCriacao = DateTime.UtcNow
                }
            }))
            .ComRota("/api/coordenador/propostas/", () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Proposta rejeitada com sucesso.")
            })
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

    // ── Issue #76 (P-05): coluna "Resumo" ────────────────────────────────────────────────
    // O toggle "Ver Resumo"/"Ocultar" foi portado da tela do Professor (que perdeu a seção de
    // propostas pendentes por completo) para cá, junto com a capacidade de decidir sobre a
    // proposta. Os casos abaixo substituem os testes por reflection de
    // TccManager.Tests.Client.Pages.Professor.DashboardTests (AlternarResumo), agora
    // exercitando o comportamento pelo clique real, não pelo campo privado.

    [Fact]
    public void Resumo_EstadoInicial_NaoExibeOTextoDaProposta()
    {
        RegistrarHttpComDadosPadrao();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));
        Assert.Contains("Ver Resumo", cut.Markup);
        Assert.DoesNotContain("Resumo completo da proposta submetida.", cut.Markup);
    }

    [Fact]
    public void Resumo_ClicarVerResumo_ExibeOTextoEAlternaORotuloParaOcultar()
    {
        RegistrarHttpComDadosPadrao();
        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Ver Resumo")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Resumo completo da proposta submetida.", cut.Markup));
        Assert.Contains("Ocultar", cut.Markup);
    }

    [Fact]
    public void Resumo_ClicarDuasVezesNaMesmaProposta_Recolhe()
    {
        RegistrarHttpComDadosPadrao();
        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Ver Resumo")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Resumo completo da proposta submetida.", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Ocultar")).Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Resumo completo da proposta submetida.", cut.Markup));
    }

    // ── Issue #76 (D9): fluxo de rejeição ────────────────────────────────────────────────
    // Portado de Pages/Professor/Dashboard.razor (POST api/orientador/propostas/{id}/rejeitar)
    // para o Coordenador (PUT api/coordenador/propostas/{id}/rejeitar). O RejeitarPropostaDialog
    // é montado de verdade aqui: a página e o diálogo compartilham o mesmo DialogService, e o
    // host <RadzenDialog/> é renderizado ao lado da página para o modal realmente abrir.

    private static readonly RenderFragment HostComDialogo = builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Dashboard>(1);
        builder.CloseComponent();
    };

    [Fact]
    public void AbrirRejeicao_ClicarRejeitar_AbreODialogoDeMotivo()
    {
        RegistrarHttpComDadosPadrao();
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));
        Assert.Single(cut.FindAll("textarea"));
    }

    [Fact]
    public void AbrirRejeicao_ConfirmarComMotivo_EnviaPutParaARotaDoCoordenadorERecarregaOsDados()
    {
        var handler = RegistrarHttpComDadosPadrao();
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar") && !b.TextContent.Contains("Proposta")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));

        cut.Find("textarea").Change("Escopo muito amplo para o prazo.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar Proposta")).Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Requisicoes, r => r.Caminho == "/api/coordenador/propostas/1/rejeitar"));

        var put = handler.Requisicoes.Single(r => r.Caminho == "/api/coordenador/propostas/1/rejeitar");
        // Verbo PUT (não POST, como era no endpoint removido do Professor) — seção 7.1 da arquitetura.
        Assert.Equal(HttpMethod.Put, put.Metodo);
        Assert.Contains("Escopo muito amplo para o prazo.", put.Corpo);

        // Sucesso => toast de sucesso + recarga do painel (KPIs e lista).
        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Success));
        Assert.True(handler.Chamadas.Count(c => c.StartsWith("/api/coordenador/dashboard-stats")) >= 2);
    }

    [Fact]
    public void AbrirRejeicao_CancelarODialogo_NaoEnviaNenhumaRequisicaoDeRejeicao()
    {
        var handler = RegistrarHttpComDadosPadrao();
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancelar")).Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Motivo da Rejeição", cut.Markup));
        Assert.DoesNotContain(handler.Requisicoes, r => r.Caminho.Contains("/rejeitar"));
    }

    [Fact]
    public void AbrirRejeicao_FalhaNoPut_ExibeNotificacaoDeErro()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/dashboard-stats", () => Json(new DashboardCoordenadorDto()))
            .ComRota("/api/coordenador/propostas-pendentes", () => Json(new List<TccResumoDto>
            {
                new() { Id = 1, Titulo = "Proposta de Teste", Resumo = "R", NomeAluno = "Aluno Um", DataCriacao = DateTime.UtcNow }
            }))
            .ComRota("/api/coordenador/propostas/", () => new HttpResponseMessage(HttpStatusCode.NotFound))
            .ComRota("/api/coordenador/professores", () => Json(new PagedResult<ProfessorResumoDto>
            {
                Items = new List<ProfessorResumoDto>(), TotalCount = 0, TotalPages = 0, CurrentPage = 1, PageSize = 100
            }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var notificationService = Services.GetRequiredService<NotificationService>();
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta de Teste", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));

        cut.Find("textarea").Change("Motivo qualquer.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar Proposta")).Click();

        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Error));
    }
}
