using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using TccManager.Client.Pages.Professor;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using Xunit;

namespace TccManager.Tests.Client.Pages.Professor;

/// <summary>
/// Issue #76 (D5) — a tela do Professor perdeu por completo a seção "Propostas Pendentes"
/// (grid, badge de contagem, toggle "Ver Resumo" e os botões Aprovar/Rejeitar), porque ela
/// listava TODAS as propostas pendentes do sistema para qualquer professor autenticado e
/// disparava ações sem nenhuma verificação de vínculo.
///
/// A versão anterior deste arquivo testava, por reflection sobre <c>new Dashboard()</c>,
/// exatamente os membros que sumiram (<c>AlternarResumo</c>/<c>propostaResumoExpandidoId</c>):
/// ela ficou obsoleta. Os casos do toggle não foram perdidos — foram portados para
/// <see cref="TccManager.Tests.Client.Pages.Coordenador.DashboardTests"/>, tela que agora
/// exibe o Resumo (P-05). Aqui o substituto é um teste bUnit de verdade (infra do commit
/// 7013518), que renderiza a página e trava tanto o que sobrou quanto o que precisa NÃO
/// existir mais.
///
/// Issue #112 — a seção "Propostas Aguardando Sua Decisão" volta à tela, mas corretamente
/// escopada: consome <c>GET api/orientador/propostas-solicitadas</c> (vínculo
/// <c>OrientadorSolicitadoId == profId</c> no WHERE da query do backend, D1/D2 da arquitetura),
/// distinto da rota antiga e insegura removida pela #76 (<c>POST api/orientador/propostas/{id}/
/// aprovar|rejeitar</c>, sem vínculo nenhum). Os testes de guarda de RBAC desta classe foram
/// ajustados para distinguir as duas rotas em vez de proibir qualquer prefixo "propostas".
/// </summary>
public class DashboardTests : BunitContext
{
    private sealed record Requisicao(HttpMethod Metodo, string Caminho, string Corpo);

    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpResponseMessage> Resposta)> _respostas = new();

        public List<string> Chamadas { get; } = new();
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
        // Radzen dispara chamadas de JS interop internas (medição de layout, etc.) que não são
        // o objeto deste teste — Loose evita falha por chamada não configurada.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        // Issue #112: a página passou a injetar DialogService (confirmação de aprovação e
        // abertura do RejeitarPropostaDialog).
        Services.AddSingleton<DialogService>();
    }

    private HandlerMultiRota RegistrarHttp(DashboardOrientadorDto dashboard, PagedResult<TccResumoDto>? propostasSolicitadas = null)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/orientador/dashboard", () => Json(dashboard))
            .ComRota("/api/orientador/propostas-solicitadas", () => Json(propostasSolicitadas ?? new PagedResult<TccResumoDto>
            {
                Items = new List<TccResumoDto>(),
                TotalCount = 0,
                TotalPages = 0,
                CurrentPage = 1,
                PageSize = 100
            }));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

    private static PagedResult<TccResumoDto> PropostaSolicitada(int id = 1, string titulo = "Proposta Solicitada", string resumo = "Resumo da proposta solicitada.", string nomeAluno = "Aluno Solicitante") => new()
    {
        Items = new List<TccResumoDto>
        {
            new() { Id = id, Titulo = titulo, Resumo = resumo, NomeAluno = nomeAluno, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow }
        },
        TotalCount = 1,
        TotalPages = 1,
        CurrentPage = 1,
        PageSize = 100
    };

    private static readonly RenderFragment HostComDialogo = builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Dashboard>(1);
        builder.CloseComponent();
    };

    private static DashboardOrientadorDto DashboardComOrientandos() => new()
    {
        OrientandosAtivos = new List<TccResumoDto>
        {
            new()
            {
                Id = 7,
                Titulo = "Análise de Algoritmos de Ordenação",
                Resumo = "Resumo do TCC do orientando.",
                NomeAluno = "Aluno Orientando",
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            }
        }
    };

    [Fact]
    public void Renderiza_ExibeOsOrientandosAtivos()
    {
        RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));
        Assert.Contains("Meus Orientandos", cut.Markup);
        Assert.Contains("Análise de Algoritmos de Ordenação", cut.Markup);
        Assert.Contains("Acessar TCC", cut.Markup);
    }

    [Fact]
    public void Renderiza_SemPropostasSolicitadas_NaoExibeBotoesDeDecisao()
    {
        // Com a lista de propostas-solicitadas vazia (default de RegistrarHttp), nenhuma linha
        // é renderizada e, portanto, nenhum botão Aprovar/Rejeitar aparece — só a seção/legenda.
        RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("Nenhuma proposta aguardando sua decisão", cut.Markup));

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Aprovar", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Rejeitar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Renderiza_NaoChamaAsRotasInsegurasRemovidasPelaIssue76()
    {
        // Issue #76: POST api/orientador/propostas/{id}/aprovar|rejeitar (sem vínculo nenhum)
        // foram removidas e continuam 404 no backend (OrientadorNotificacaoIntegracao_Tests).
        // Issue #112 reintroduz, de propósito, GET/PUT api/orientador/propostas-solicitadas/...
        // (vínculo no WHERE da query) — este guard distingue as duas rotas pelo prefixo exato.
        var handler = RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));

        Assert.DoesNotContain(handler.Chamadas, c => c.StartsWith("/api/orientador/propostas/", StringComparison.Ordinal));
        Assert.Contains(handler.Chamadas, c => c.StartsWith("/api/orientador/propostas-solicitadas", StringComparison.Ordinal));
    }

    [Fact]
    public void Renderiza_ChamaODashboardSemParametrosDePaginacao()
    {
        // D3: GetDashboard perdeu o PaginacaoQuery junto com a lista de pendentes — a página não
        // pode continuar mandando page/pageSize (ficaria sugerindo um contrato que não existe).
        var handler = RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));

        var chamada = Assert.Single(handler.Chamadas, c => c.StartsWith("/api/orientador/dashboard", StringComparison.Ordinal));
        Assert.Equal("/api/orientador/dashboard", chamada);
    }

    [Fact]
    public void SemOrientandos_ExibeAlertaDeListaVazia()
    {
        RegistrarHttp(new DashboardOrientadorDto());

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("ainda não possui orientandos ativos", cut.Markup));
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
        // Mesmo guard de saída do teste equivalente da tela do Coordenador: a página interpola
        // @tcc.NomeAluno como texto puro, nunca via MarkupString.
        RegistrarHttp(new DashboardOrientadorDto
        {
            OrientandosAtivos = new List<TccResumoDto>
            {
                new() { Id = 1, Titulo = "TCC", NomeAluno = "<script>alert(1)</script>", DataCriacao = DateTime.UtcNow }
            }
        });

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("TCC", cut.Markup));
        Assert.Empty(cut.FindAll("script"));
    }

    // ── Issue #112: seção "Propostas Aguardando Sua Decisão" ──────────────────────────────

    [Fact]
    public void PropostasSolicitadas_ComItens_ExibeNaGrid()
    {
        RegistrarHttp(DashboardComOrientandos(), PropostaSolicitada());

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Solicitante", cut.Markup));
        Assert.Contains("Proposta Solicitada", cut.Markup);
        Assert.Contains("Propostas Aguardando Sua Decisão", cut.Markup);
    }

    [Fact]
    public void PropostasSolicitadas_AprovarComConfirmacao_EnviaPutSemCorpoERecarregaALista()
    {
        var handler = RegistrarHttp(DashboardComOrientandos(), PropostaSolicitada());
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta Solicitada", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Aprovar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Aprovar Proposta", cut.Markup));

        // Dois botões "Aprovar" na tela a partir daqui (o da linha da grid e o do modal de
        // confirmação) — o clique tem que ser no do modal.
        cut.FindAll(".rz-dialog button").Single(b => b.TextContent.Contains("Aprovar", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Requisicoes, r => r.Caminho == "/api/orientador/propostas-solicitadas/1/aprovar"));

        var put = handler.Requisicoes.Single(r => r.Caminho == "/api/orientador/propostas-solicitadas/1/aprovar");
        Assert.Equal(HttpMethod.Put, put.Metodo);
        Assert.Equal(string.Empty, put.Corpo);

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Success));
        // Recarrega a lista: pelo menos 2 chamadas à rota de propostas-solicitadas (inicial + reload).
        Assert.True(handler.Chamadas.Count(c => c.StartsWith("/api/orientador/propostas-solicitadas")) >= 2);
    }

    [Fact]
    public void PropostasSolicitadas_RejeitarComMotivo_EnviaPutComMotivoERecarregaALista()
    {
        var handler = RegistrarHttp(DashboardComOrientandos(), PropostaSolicitada());
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta Solicitada", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));

        cut.Find("textarea").Change("Escopo incompativel com minha linha de pesquisa.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar Proposta")).Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Requisicoes, r => r.Caminho == "/api/orientador/propostas-solicitadas/1/rejeitar"));

        var put = handler.Requisicoes.Single(r => r.Caminho == "/api/orientador/propostas-solicitadas/1/rejeitar");
        Assert.Equal(HttpMethod.Put, put.Metodo);
        Assert.Contains("Escopo incompativel com minha linha de pesquisa.", put.Corpo);

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Success));
    }

    [Fact]
    public void PropostasSolicitadas_RejeitarCancelarDialogo_NaoEnviaRequisicao()
    {
        var handler = RegistrarHttp(DashboardComOrientandos(), PropostaSolicitada());
        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta Solicitada", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Rejeitar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Motivo da Rejeição", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Cancelar")).Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Motivo da Rejeição", cut.Markup));
        Assert.DoesNotContain(handler.Requisicoes, r => r.Caminho.Contains("/rejeitar"));
    }

    [Fact]
    public void PropostasSolicitadas_AprovarComConflito409_ExibeMensagemAmigavelERecarregaALista()
    {
        // D5 da arquitetura: 409 é um desfecho esperado (Coordenador decidiu primeiro) — a tela
        // deve mostrar mensagem amigável (não erro genérico) e recarregar a lista.
        var handler = new HandlerMultiRota()
            .ComRota("/api/orientador/dashboard", () => Json(DashboardComOrientandos()))
            .ComRota("/api/orientador/propostas-solicitadas/1/aprovar", () => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("Esta proposta já foi decidida por outra ação simultânea. Atualize a lista e tente novamente.")
            })
            .ComRota("/api/orientador/propostas-solicitadas", () => Json(PropostaSolicitada()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Proposta Solicitada", cut.Markup));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Aprovar")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Aprovar Proposta", cut.Markup));
        cut.FindAll(".rz-dialog button").Single(b => b.TextContent.Contains("Aprovar", StringComparison.Ordinal)).Click();

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Warning));
        Assert.True(handler.Chamadas.Count(c => c.StartsWith("/api/orientador/propostas-solicitadas") && !c.Contains("/aprovar")) >= 2);
    }
}
