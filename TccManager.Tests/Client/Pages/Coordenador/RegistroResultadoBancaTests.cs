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
/// Issue #75 — <see cref="RegistroResultadoBanca"/> nunca teve nenhum teste. O fluxo de
/// "Registrar Resultado" (<c>DialogService.OpenAsync&lt;RegistrarResultadoDialog&gt;</c>) não é
/// exercitado por clique aqui pelo mesmo motivo documentado em
/// <see cref="TccManager.Tests.Client.Pages.Coordenador.GestaoProfessoresTests"/>: precisaria
/// de um host <c>&lt;RadzenDialog/&gt;</c> real montado para o modal abrir/fechar de verdade.
/// </summary>
public class RegistroResultadoBancaTests : BunitContext
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

    private static BancaPendenteDto NovaBanca(int tccId = 1, bool comMembroExterno = false) => new()
    {
        TccId = tccId,
        DataHora = DateTime.UtcNow.AddDays(1),
        Local = "Sala 202",
        TccTitulo = "TCC Pendente de Resultado",
        NomeAluno = "Aluno Pendente",
        MembrosExternos = comMembroExterno
            ? new List<MembroExternoBancaDto> { new() { MembroExternoId = 5, Nome = "Dra. Externa" } }
            : new List<MembroExternoBancaDto>()
    };

    public RegistroResultadoBancaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    [Fact]
    public void SemBancasPendentes_ExibeAlertaDeNenhumaBancaPendente()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto>()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<RegistroResultadoBanca>();

        cut.WaitForAssertion(() => Assert.Contains(
            "Não há nenhuma banca aguardando lançamento de nota", cut.Markup));
    }

    [Fact]
    public void ComBancaPendente_ExibeOsDadosNaGrid()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto> { NovaBanca() }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<RegistroResultadoBanca>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Pendente", cut.Markup));
        Assert.Contains("TCC Pendente de Resultado", cut.Markup);
    }

    [Fact]
    public void SemMembroExterno_NaoExibeBotaoDeReenviarRascunho()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto> { NovaBanca(comMembroExterno: false) }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<RegistroResultadoBanca>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Pendente", cut.Markup));
        Assert.DoesNotContain("Reenviar Rascunho", cut.Markup);
    }

    [Fact]
    public void ComMembroExterno_ExibeBotaoDeReenviarRascunho()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto> { NovaBanca(comMembroExterno: true) }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<RegistroResultadoBanca>();

        cut.WaitForAssertion(() => Assert.Contains("Reenviar Rascunho", cut.Markup));
    }

    [Fact]
    public void BaixarRascunho_ComSucesso_ChamaDownloadFileFromBytesComNomeCorreto()
    {
        var pdfFalso = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto> { NovaBanca(tccId: 7) }))
            .ComRota("/api/coordenador/banca/7/ata-rascunho-pdf", () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pdfFalso)
            });
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);

        var cut = Render<RegistroResultadoBanca>();
        cut.WaitForAssertion(() => Assert.Contains("Rascunho", cut.Markup));

        var botao = cut.FindAll("button").Single(b => b.TextContent.Contains("Rascunho") && !b.TextContent.Contains("Reenviar"));
        botao.Click();

        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        Assert.Equal("ata-rascunho-7.pdf", download.Invocations.Single().Arguments[0]);
    }

    [Fact]
    public void BaixarRascunho_ComFalha_ExibeNotificacaoDeErro()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/bancas-pendentes-resultado", () => Json(new List<BancaPendenteDto> { NovaBanca(tccId: 7) }))
            .ComRota("/api/coordenador/banca/7/ata-rascunho-pdf", () => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Banca não encontrada.")
            });
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<RegistroResultadoBanca>();
        cut.WaitForAssertion(() => Assert.Contains("Rascunho", cut.Markup));

        var botao = cut.FindAll("button").Single(b => b.TextContent.Contains("Rascunho") && !b.TextContent.Contains("Reenviar"));
        botao.Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
    }

    [Fact]
    public void ErroDeConexaoAoCarregarBancas_ExibeNotificacaoDeErro_NaoLancaExcecaoNaoTratada()
    {
        var handler = new HandlerMultiRota(); // nenhuma rota -> 404, GetFromJsonAsync lança
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<RegistroResultadoBanca>();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
    }
}
