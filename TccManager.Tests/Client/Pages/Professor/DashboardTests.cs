using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
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
        // Radzen dispara chamadas de JS interop internas (medição de layout, etc.) que não são
        // o objeto deste teste — Loose evita falha por chamada não configurada.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
    }

    private HandlerMultiRota RegistrarHttp(DashboardOrientadorDto dashboard)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/orientador/dashboard", () => Json(dashboard));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

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
    public void Renderiza_NaoExibeNadaDePropostasPendentes()
    {
        // Guard de regressão do achado de RBAC: se alguém reintroduzir a seção na tela do
        // Professor, este teste fica vermelho antes de o endpoint sequer ser chamado.
        RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));

        Assert.DoesNotContain("Propostas Pendentes", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ver Resumo", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Aprovar", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Rejeitar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Renderiza_NaoChamaNenhumaRotaDePropostasDoOrientador()
    {
        var handler = RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));

        Assert.DoesNotContain(handler.Chamadas, c => c.StartsWith("/api/orientador/propostas", StringComparison.Ordinal));
    }

    [Fact]
    public void Renderiza_ChamaODashboardSemParametrosDePaginacao()
    {
        // D3: GetDaboard perdeu o PaginacaoQuery junto com a lista de pendentes — a página não
        // pode continuar mandando page/pageSize (ficaria sugerindo um contrato que não existe).
        var handler = RegistrarHttp(DashboardComOrientandos());

        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Orientando", cut.Markup));

        var chamada = Assert.Single(handler.Chamadas);
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
}
