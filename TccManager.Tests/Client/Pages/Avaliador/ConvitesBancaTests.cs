using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Pages.Avaliador;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Client.Pages.Avaliador;

/// <summary>
/// Issue #75 — <see cref="ConvitesBanca"/> só tinha um teste de reflection sobre
/// <c>new ConvitesBanca()</c> (nenhum HTTP, nenhum JS interop, nenhuma renderização real —
/// ver histórico do arquivo). Reescrito com bUnit: layout de cards, estados vazio/carregando,
/// e os três fluxos de download (rascunho, versão final, e os erros 403/404) agora são
/// exercitados de verdade.
/// </summary>
public class ConvitesBancaTests : BunitContext
{
    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpRequestMessage, HttpResponseMessage> Resposta)> _respostas = new();

        public List<string> Chamadas { get; } = new();

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
            var caminho = request.RequestUri!.PathAndQuery;
            Chamadas.Add(caminho);

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (caminho.StartsWith(prefixo, StringComparison.Ordinal))
                    return Task.FromResult(resposta(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    private static ConviteBancaDto NovoConvite(
        int bancaId = 1, int? arquivoFinalEntregaId = null, string? arquivoFinalExtensao = null) => new()
    {
        BancaId = bancaId,
        TccTitulo = "TCC de Teste",
        NomeAluno = "Aluno Um",
        NomeOrientador = "Orientador Um",
        DataHora = DateTime.UtcNow.AddDays(-1),
        Local = "Sala 101",
        ArquivoFinalEntregaId = arquivoFinalEntregaId,
        ArquivoFinalExtensao = arquivoFinalExtensao
    };

    public ConvitesBancaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
    }

    [Fact]
    public void SemConvites_ExibeAlertaDeNenhumConviteEncontrado()
    {
        var handler = new HandlerMultiRota().ComRota("/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto>()));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<ConvitesBanca>();

        cut.WaitForAssertion(() => Assert.Contains("Nenhum convite encontrado", cut.Markup));
    }

    [Fact]
    public void ComConvite_ExibeOsDadosDoCard()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto> { NovoConvite() }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<ConvitesBanca>();

        cut.WaitForAssertion(() => Assert.Contains("TCC de Teste", cut.Markup));
        Assert.Contains("Aluno Um", cut.Markup);
        Assert.Contains("Orientador Um", cut.Markup);
        Assert.Contains("Sala 101", cut.Markup);
    }

    [Fact]
    public void SemEntregaFinal_ExibeBotaoArquivoIndisponivelDesabilitado()
    {
        var handler = new HandlerMultiRota().ComRota(
            "/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto> { NovoConvite(arquivoFinalEntregaId: null) }));
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        var cut = Render<ConvitesBanca>();

        cut.WaitForAssertion(() => Assert.Contains("Arquivo Indisponível", cut.Markup));
    }

    [Fact]
    public void BaixarRascunho_ComSucesso_ChamaDownloadFileFromBytesComNomeCorreto()
    {
        var pdfFalso = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // "%PDF"
        var handler = new HandlerMultiRota()
            .ComRota("/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto> { NovoConvite(bancaId: 42) }))
            .ComRota("/api/avaliador/banca/42/ata-rascunho-pdf", () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pdfFalso)
            });
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);

        var cut = Render<ConvitesBanca>();
        cut.WaitForAssertion(() => Assert.Contains("Baixar Rascunho da Ata", cut.Markup));

        var botao = cut.FindAll("button").Single(b => b.TextContent.Contains("Baixar Rascunho da Ata"));
        botao.Click();

        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        var argumentos = download.Invocations.Single().Arguments;
        Assert.Equal("ata-rascunho-42.pdf", argumentos[0]);
        Assert.Equal("application/pdf", argumentos[1]);
    }

    [Fact]
    public void BaixarRascunho_ComFalha_ExibeNotificacaoDeErro_NaoChamaDownload()
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto> { NovoConvite(bancaId: 42) }))
            .ComRota("/api/avaliador/banca/42/ata-rascunho-pdf", () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Você não é avaliador desta banca.")
            });
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<ConvitesBanca>();
        cut.WaitForAssertion(() => Assert.Contains("Baixar Rascunho da Ata", cut.Markup));

        var botao = cut.FindAll("button").Single(b => b.TextContent.Contains("Baixar Rascunho da Ata"));
        botao.Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
        Assert.Empty(download.Invocations);
    }

    [Fact]
    public void BaixarArquivoFinal_ComSucesso_ChamaDownloadFileFromBytesComExtensaoCorreta()
    {
        var arquivoFalso = new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // assinatura ZIP/DOCX
        var handler = new HandlerMultiRota()
            .ComRota("/api/avaliador/meus-convites", () => Json(new List<ConviteBancaDto>
            {
                NovoConvite(bancaId: 1, arquivoFinalEntregaId: 99, arquivoFinalExtensao: ".docx")
            }))
            .ComRota("/api/tcc/entregas/99/download", () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(arquivoFalso)
            });
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);

        var cut = Render<ConvitesBanca>();
        cut.WaitForAssertion(() => Assert.Contains("Baixar Versão Final (TCC)", cut.Markup));

        var botao = cut.FindAll("button").Single(b => b.TextContent.Contains("Baixar Versão Final"));
        botao.Click();

        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        Assert.Equal("entrega-99.docx", download.Invocations.Single().Arguments[0]);
    }

    [Fact]
    public void ErroDeConexaoAoCarregarConvites_ExibeNotificacaoDeErro()
    {
        var handler = new HandlerMultiRota(); // nenhuma rota configurada -> 404, GetFromJsonAsync lança
        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<ConvitesBanca>();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        Assert.Equal(NotificationSeverity.Error, notificationService.Messages.First().Severity);
    }
}
