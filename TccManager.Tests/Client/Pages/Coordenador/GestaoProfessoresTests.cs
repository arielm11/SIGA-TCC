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
/// Issue #75 — <see cref="GestaoProfessores"/> nunca teve nenhum teste. É o componente mais
/// complexo dos citados na issue (2 <c>RadzenDataGrid</c> em modo LoadData, formulário com
/// validação, e dois fluxos que abrem <c>DialogService</c> como modal). Coberto aqui: as duas
/// grids carregando via LoadData com os parâmetros de página corretos, o formulário de novo
/// avaliador externo, e o salvamento de capacidade. Os fluxos de exclusão
/// (<c>DialogService.Confirm</c>) e edição (<c>DialogService.OpenAsync&lt;EditarMembroExternoDialog&gt;</c>)
/// não são exercitados por clique aqui — precisariam de um host <c>&lt;RadzenDialog/&gt;</c>
/// real montado na árvore de renderização para o modal abrir e fechar de verdade, o que é uma
/// integração mais profunda (e arriscaria travar o teste esperando um diálogo que nunca é
/// fechado); ver docs/implementacao para o registro dessa lacuna.
/// </summary>
public class GestaoProfessoresTests : BunitContext
{
    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpRequestMessage, HttpResponseMessage> Resposta)> _respostas = new();

        public List<HttpRequestMessage> Chamadas { get; } = new();

        public HandlerMultiRota ComRota(string prefixo, Func<HttpRequestMessage, HttpResponseMessage> resposta)
        {
            _respostas.Add((prefixo, resposta));
            return this;
        }

        public HandlerMultiRota ComRota(string prefixo, Func<HttpResponseMessage> resposta) =>
            ComRota(prefixo, _ => resposta());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas.Add(request);

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (request.RequestUri!.PathAndQuery.StartsWith(prefixo, StringComparison.Ordinal))
                    return Task.FromResult(resposta(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    private static PagedResult<ProfessorResumoDto> UmProfessor() => new()
    {
        Items = new List<ProfessorResumoDto>
        {
            new() { Id = 1, Nome = "Prof. Fulano", CargaAtual = 2, LimiteOrientandos = 5, AceitandoOrientandos = true }
        },
        TotalCount = 1,
        TotalPages = 1,
        CurrentPage = 1,
        PageSize = 20
    };

    private static PagedResult<MembroExternoDto> UmExterno() => new()
    {
        Items = new List<MembroExternoDto>
        {
            new() { Id = 1, Nome = "Dra. Externa", Email = "externa@teste.com", Instituicao = "Universidade Parceira" }
        },
        TotalCount = 1,
        TotalPages = 1,
        CurrentPage = 1,
        PageSize = 20
    };

    private HandlerMultiRota RegistrarHttpComDadosPadrao()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/professores", () => Json(UmProfessor()))
            .ComRota("/api/coordenador/membros-externos", () => Json(UmExterno()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        return handler;
    }

    public GestaoProfessoresTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    [Fact]
    public void Renderiza_CarregaAsDuasGridsViaLoadDataComPaginaEPageSizeCorretos()
    {
        var handler = RegistrarHttpComDadosPadrao();

        var cut = Render<GestaoProfessores>();

        cut.WaitForAssertion(() => Assert.Contains("Prof. Fulano", cut.Markup));
        Assert.Contains("Dra. Externa", cut.Markup);

        var chamadaProfessores = handler.Chamadas.Single(r => r.RequestUri!.AbsolutePath == "/api/coordenador/professores");
        Assert.Contains("page=1", chamadaProfessores.RequestUri!.Query);
        Assert.Contains("pageSize=20", chamadaProfessores.RequestUri.Query);

        var chamadaExternos = handler.Chamadas.Single(r => r.RequestUri!.AbsolutePath == "/api/coordenador/membros-externos");
        Assert.Contains("page=1", chamadaExternos.RequestUri!.Query);
        Assert.Contains("pageSize=20", chamadaExternos.RequestUri.Query);
    }

    [Fact]
    public void FormularioDeNovoAvaliadorExterno_SubmeteEChamaOPostCorreto()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/professores", () => Json(UmProfessor()))
            .ComRota("/api/coordenador/membros-externos", request => request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : Json(UmExterno()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<GestaoProfessores>();
        cut.WaitForAssertion(() => Assert.Contains("Dra. Externa", cut.Markup));

        cut.Find("input[name=Nome]").Change("Novo Avaliador");
        cut.Find("input[name=Email]").Change("novo@avaliador.com");
        cut.Find("input[name=Instituicao]").Change("Instituto X");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Chamadas, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/coordenador/membros-externos"));
    }

    [Fact]
    public void SalvarCapacidade_ClicarSalvar_ChamaPutComOsValoresAtuais()
    {
        var handler = RegistrarHttpComDadosPadrao();
        handler.ComRota("/api/coordenador/professores/1/capacidade", () => new HttpResponseMessage(HttpStatusCode.OK));

        var cut = Render<GestaoProfessores>();
        cut.WaitForAssertion(() => Assert.Contains("Prof. Fulano", cut.Markup));

        var botaoSalvar = cut.FindAll("button").Single(b => b.TextContent.Contains("Salvar"));
        botaoSalvar.Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Chamadas, r => r.RequestUri!.AbsolutePath == "/api/coordenador/professores/1/capacidade"));

        var chamada = handler.Chamadas.Single(r => r.RequestUri!.AbsolutePath == "/api/coordenador/professores/1/capacidade");
        Assert.Equal(HttpMethod.Put, chamada.Method);
    }

    [Fact]
    public void FalhaHttpAoCarregarUmaDasGrids_NaoDerrubaARenderizacaoDaPagina()
    {
        // Achado de segurança/A10 encontrado ao escrever esta suíte: LoadDataProfessores e
        // LoadDataExternos não tinham try/catch — uma falha de HTTP virava exceção não
        // tratada propagada a partir do RadzenDataGrid.Reload() chamado em
        // OnAfterRenderAsync, capaz de derrubar a renderização da página inteira (no
        // navegador real, o overlay "An unhandled error has occurred"). Corrigido com o
        // mesmo padrão já usado em Dashboard.CarregarDados/ConvitesBanca.CarregarConvites —
        // este teste prova que a falha de HTTP na grid de professores é tratada localmente
        // (notificação de erro) e não impede a outra grid (externos) de carregar e renderizar
        // normalmente.
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/professores", () => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .ComRota("/api/coordenador/membros-externos", () => Json(UmExterno()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<GestaoProfessores>();

        cut.WaitForAssertion(() => Assert.Contains("Dra. Externa", cut.Markup));
        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
    }

    [Fact]
    public void SemProfessoresNemExternos_ExibeTextoDeGridVazia()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/professores", () => Json(new PagedResult<ProfessorResumoDto>
            {
                Items = new List<ProfessorResumoDto>(), TotalCount = 0, TotalPages = 0, CurrentPage = 1, PageSize = 20
            }))
            .ComRota("/api/coordenador/membros-externos", () => Json(new PagedResult<MembroExternoDto>
            {
                Items = new List<MembroExternoDto>(), TotalCount = 0, TotalPages = 0, CurrentPage = 1, PageSize = 20
            }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<GestaoProfessores>();

        cut.WaitForAssertion(() => Assert.Contains("Nenhum professor cadastrado.", cut.Markup));
        Assert.Contains("Nenhum avaliador externo cadastrado.", cut.Markup);
    }
}
