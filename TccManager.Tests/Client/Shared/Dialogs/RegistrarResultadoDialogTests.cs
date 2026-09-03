using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using TccManager.Client.Shared.Dialogs;
using Xunit;

namespace TccManager.Tests.Client.Shared.Dialogs;

/// <summary>
/// Issue #83 (D10) — rótulo e legenda do campo de upload de <c>RegistrarResultadoDialog</c>.
///
/// A causa raiz da issue é de vocabulário tanto quanto de código: o campo se chamava "Arquivo
/// da Ata Assinada (PDF)", o que dava a entender que o arquivo anexado ERA a ata oficial —
/// quando a ata oficial é a gerada pelo sistema, e o anexo é uma cópia complementar que, até
/// esta issue, nem sequer podia ser baixada de volta.
///
/// A arquitetura (seção 12.3, item 14) registrou que este diálogo não tinha teste bUnit
/// próprio porque a *página* o abre via <c>DialogService.OpenAsync</c>, que exige um host
/// <c>&lt;RadzenDialog/&gt;</c>. Aqui o componente é renderizado DIRETAMENTE (é um componente
/// Blazor comum, com dois [Parameter]) — sem host de diálogo, sem exercitar abrir/fechar. Isso
/// cobre a fase de formulário; o que depende de upload real de arquivo fica fora (ver o resumo
/// em docs/testes).
/// </summary>
public class RegistrarResultadoDialogTests : BunitContext
{
    public RegistrarResultadoDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
        Services.AddScoped(_ => new HttpClient(new HandlerSempre404()) { BaseAddress = new Uri("https://localhost/") });
    }

    private sealed class HandlerSempre404 : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private IRenderedComponent<RegistrarResultadoDialog> RenderizarDialogo() =>
        Render<RegistrarResultadoDialog>(parameters => parameters
            .Add(p => p.BancaId, 7)
            .Add(p => p.NomeAluno, "Aluno de Teste"));

    [Fact]
    public void CampoDeUpload_UsaORotuloNovoDeCopiaAssinada()
    {
        var cut = RenderizarDialogo();

        Assert.Contains("Cópia assinada da ata (PDF)", cut.Markup);
    }

    [Fact]
    public void CampoDeUpload_NaoUsaMaisORotuloAntigoQueSugeriaSerAAtaOficial()
    {
        // Regressão da causa raiz: "Arquivo da Ata Assinada (PDF)" sugeria que o anexo era o
        // documento oficial da defesa.
        var cut = RenderizarDialogo();

        Assert.DoesNotContain("Arquivo da Ata Assinada", cut.Markup);
    }

    [Fact]
    public void CampoDeUpload_ExibeALegendaExplicandoQueOArquivoEhComplementar()
    {
        var cut = RenderizarDialogo();

        Assert.Contains("Documento complementar", cut.Markup);
        Assert.Contains("A ata oficial é a gerada automaticamente pelo sistema", cut.Markup);
        Assert.Contains("disponível para download pela Coordenação", cut.Markup);
    }

    [Fact]
    public void Upload_MantemOFiltroClientSideDeExtensaoPdf()
    {
        // Alinhado com a whitelist server-side de D6: o RadzenUpload já filtrava .pdf e isso
        // não podia ser afrouxado — se alguém trocar por "*", o usuário passa a conseguir
        // escolher um arquivo que o backend recusa com 400.
        var cut = RenderizarDialogo();

        Assert.Equal(".pdf", cut.FindComponent<RadzenUpload>().Instance.Accept);
    }

    [Fact]
    public void ConfirmarFicaDesabilitadoEnquantoNenhumArquivoForSelecionado()
    {
        // Regressão do comportamento existente: a legenda nova foi inserida no mesmo <div> do
        // upload; o botão de confirmação continua dependendo do arquivo (e da nota).
        var cut = RenderizarDialogo();

        var confirmar = cut.FindComponents<RadzenButton>()
            .Single(b => b.Instance.Icon == "save");

        Assert.True(confirmar.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public void DialogoAbreNaFaseDeFormulario_NaoNaTelaDeSucesso()
    {
        // Guarda de sanidade da fase inicial: o botão de download da ata gerada só existe na
        // tela de sucesso, depois de POST bem-sucedido.
        var cut = RenderizarDialogo();

        Assert.Contains("Aluno: Aluno de Teste", cut.Markup);
        Assert.DoesNotContain("Baixar ata gerada (PDF)", cut.Markup);
    }
}
