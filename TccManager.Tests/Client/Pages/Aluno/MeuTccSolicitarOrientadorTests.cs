using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Pages.Aluno;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Client.Pages.Aluno;

/// <summary>
/// Issue #112 — tela do Aluno (<see cref="MeuTcc"/>): dropdown opcional de professor na
/// submissão da proposta (RF01/6.4 da arquitetura), exibição do professor solicitado enquanto a
/// proposta está Pendente (RF04/D7) e texto neutro de rejeição (D9 — o sistema não guarda se
/// quem rejeitou foi o Professor solicitado ou a Coordenação).
///
/// A interação real com o <c>RadzenDropDown</c> (clique no item da lista de opções, que o Radzen
/// mantém no DOM mesmo fechado) foi validada antes de escrever estes testes — não é reflection
/// sobre o componente, é o mesmo <c>@bind-Value</c> que o backend recebe de verdade.
/// </summary>
public class MeuTccSolicitarOrientadorTests : BunitContext
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

    public MeuTccSolicitarOrientadorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    private static PagedResult<ProfessorResumoDto> ListaComUmProfessor() => new()
    {
        Items = new List<ProfessorResumoDto>
        {
            new() { Id = 10, Nome = "Prof. Teste", CargaAtual = 2, LimiteOrientandos = 5, AceitandoOrientandos = true }
        },
        TotalCount = 1,
        TotalPages = 1,
        CurrentPage = 1,
        PageSize = 100
    };

    private HandlerMultiRota RegistrarHttp(Tcc? tccExistente = null, PagedResult<ProfessorResumoDto>? professores = null)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/tcc/meu-tcc", () => tccExistente == null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(tccExistente))
            .ComRota("/api/tcc/professores", () => Json(professores ?? ListaComUmProfessor()))
            .ComRota("/api/tcc/proposta", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") })
            .ComRota("/api/tcc/entregas", () => Json(new PagedResult<Entrega> { Items = new(), TotalCount = 0, TotalPages = 0, CurrentPage = 1, PageSize = 100 }))
            .ComRota("/api/tcc/acompanhamentos", () => Json(new List<Acompanhamento>()))
            .ComRota("/api/tcc/minha-banca", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

    // ── Dropdown de professor na submissão (RF01/6.4) ──────────────────────────────────────

    [Fact]
    public void SemTccExistente_CarregaListaDeProfessores_ExibeOComboDeSelecao()
    {
        RegistrarHttp(professores: ListaComUmProfessor());

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Professor desejado", cut.Markup));
        Assert.Contains("Prof. Teste (Carga: 2/5)", cut.Markup);
    }

    [Fact]
    public void SelecionarProfessorESubmeter_EnviaOrientadorSolicitadoIdNoCorpo()
    {
        var handler = RegistrarHttp(professores: ListaComUmProfessor());

        var cut = Render<MeuTcc>();
        cut.WaitForAssertion(() => Assert.Contains("Prof. Teste (Carga: 2/5)", cut.Markup));

        cut.Find("li[aria-label='Prof. Teste (Carga: 2/5)']").Click();
        cut.Find("input[name='Titulo']").Change("Título de Teste");
        cut.Find("textarea[name='Resumo']").Change("Resumo de teste.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Submeter Proposta")).Click();

        cut.WaitForAssertion(() => Assert.Contains(handler.Requisicoes, r => r.Caminho == "/api/tcc/proposta"));

        var post = handler.Requisicoes.Single(r => r.Caminho == "/api/tcc/proposta");
        Assert.Equal(HttpMethod.Post, post.Metodo);
        Assert.Contains("\"orientadorSolicitadoId\":10", post.Corpo);
    }

    [Fact]
    public void SubmeterSemSelecionarProfessor_NaoEnviaUmIdDeProfessor()
    {
        var handler = RegistrarHttp(professores: ListaComUmProfessor());

        var cut = Render<MeuTcc>();
        cut.WaitForAssertion(() => Assert.Contains("Professor desejado", cut.Markup));

        cut.Find("input[name='Titulo']").Change("Título de Teste");
        cut.Find("textarea[name='Resumo']").Change("Resumo de teste.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Submeter Proposta")).Click();

        cut.WaitForAssertion(() => Assert.Contains(handler.Requisicoes, r => r.Caminho == "/api/tcc/proposta"));

        var post = handler.Requisicoes.Single(r => r.Caminho == "/api/tcc/proposta");
        Assert.DoesNotContain("\"orientadorSolicitadoId\":10", post.Corpo);
    }

    [Fact]
    public void TccReprovado_TambemExibeOComboDeSelecaoDeProfessor()
    {
        // O aluno rejeitado submete uma nova proposta (decisão de produto 4.3) — o formulário,
        // e portanto o combo de professor, tem que reaparecer também neste estado.
        var tcc = new Tcc { Id = 1, Titulo = "Antiga", Resumo = "R", Status = StatusTcc.Reprovado, MotivoRejeicao = "Não serve." };
        RegistrarHttp(tcc, ListaComUmProfessor());

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Professor desejado", cut.Markup));
        Assert.Contains("Prof. Teste (Carga: 2/5)", cut.Markup);
    }

    [Fact]
    public void TccAprovado_NaoChamaAApiDeProfessores()
    {
        // O formulário de submissão não aparece com TCC já Aprovado/EmAndamento — não há porquê
        // buscar a lista de professores nesse estado.
        var tcc = new Tcc { Id = 1, Titulo = "Aprovado", Resumo = "R", Status = StatusTcc.Aprovado };
        var handler = RegistrarHttp(tcc);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Nova Entrega", cut.Markup));
        Assert.DoesNotContain(handler.Chamadas, c => c.StartsWith("/api/tcc/professores", StringComparison.Ordinal));
    }

    // ── Exibição do professor solicitado enquanto Pendente (RF04/D7) ───────────────────────

    [Fact]
    public void TccPendenteComProfessorSolicitado_ExibeNomeDoProfessorEAguardandoAprovacao()
    {
        var tcc = new Tcc
        {
            Id = 1,
            Titulo = "Proposta Pendente",
            Resumo = "R",
            Status = StatusTcc.Pendente,
            NomeOrientadorSolicitado = "Prof. Fulano de Tal"
        };
        RegistrarHttp(tcc);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Proposta enviada para:", cut.Markup));
        Assert.Contains("Prof. Fulano de Tal", cut.Markup);
        Assert.Contains("aguardando aprovação", cut.Markup);
    }

    [Fact]
    public void TccPendenteSemProfessorSolicitado_NaoExibeLinhaDeProfessorSolicitado()
    {
        var tcc = new Tcc { Id = 1, Titulo = "Proposta Pendente", Resumo = "R", Status = StatusTcc.Pendente, NomeOrientadorSolicitado = null };
        RegistrarHttp(tcc);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Sua proposta está sendo analisada", cut.Markup));
        Assert.DoesNotContain("Proposta enviada para:", cut.Markup);
    }

    // ── Texto neutro de rejeição (D9) ───────────────────────────────────────────────────────

    [Fact]
    public void TccRejeitado_ExibeTextoNeutro_NaoAtribuiARejeicaoAColegiado()
    {
        // D9 da arquitetura: o sistema não guarda se quem rejeitou foi o Professor solicitado
        // ou a Coordenação — o texto não pode mais dizer "rejeitada pela Coordenação".
        var tcc = new Tcc { Id = 1, Titulo = "Proposta Antiga", Resumo = "R", Status = StatusTcc.Reprovado, MotivoRejeicao = "Fora de escopo." };
        RegistrarHttp(tcc, ListaComUmProfessor());

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Proposta Rejeitada", cut.Markup));
        Assert.Contains("foi avaliada e não foi aprovada", cut.Markup);
        Assert.DoesNotContain("rejeitada pela Coordenação", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
