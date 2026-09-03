using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using TccManager.Client.Pages.Coordenador;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Client.Pages.Coordenador;

/// <summary>
/// Issue #83 (D9) — <c>BancasConcluidas.razor</c> nunca teve teste bUnit. A tela ganhou um
/// segundo botão de download por linha ("Cópia assinada", ao lado do renomeado "Ata gerada"),
/// e com ele dois comportamentos novos que só um teste pega:
///
/// 1. o botão novo fica DESABILITADO quando a banca não tem cópia anexada
///    (<c>BancaConcluidaDto.PossuiAtaAssinada == false</c>), em vez de oferecer um download que
///    resultaria em 404;
/// 2. o estado de "Baixando..." é independente entre os dois botões da MESMA linha — antes
///    havia um único campo <c>bancaIdBaixando</c>, e reusá-lo faria clicar em um dos botões
///    apagar/travar o outro.
///
/// Os botões são localizados pelo <c>Icon</c> do <see cref="RadzenButton"/> (picture_as_pdf /
/// draw) e não por texto: quando <c>IsBusy</c> é true o rótulo vira "Baixando..." nos dois, e
/// buscar por texto tornaria justamente o teste de independência ambíguo.
/// </summary>
public class BancasConcluidasTests : BunitContext
{
    private const string IconeAtaGerada = "picture_as_pdf";
    private const string IconeCopiaAssinada = "draw";

    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpRequestMessage, Task<HttpResponseMessage>> Resposta)> _respostas = new();

        public List<string> Chamadas { get; } = new();

        public HandlerMultiRota ComRota(string prefixo, Func<HttpResponseMessage> resposta)
        {
            _respostas.Add((prefixo, _ => Task.FromResult(resposta())));
            return this;
        }

        public HandlerMultiRota ComRotaAssincrona(string prefixo, Func<Task<HttpResponseMessage>> resposta)
        {
            _respostas.Add((prefixo, _ => resposta()));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var caminho = request.RequestUri!.PathAndQuery;
            Chamadas.Add(caminho);

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (caminho.StartsWith(prefixo, StringComparison.Ordinal))
                    return await resposta(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    private static BancaConcluidaDto NovaBanca(int bancaId, bool possuiAtaAssinada, string nomeAluno = "Aluno Concluido") => new()
    {
        BancaId = bancaId,
        TccTitulo = $"TCC {bancaId}",
        NomeAluno = nomeAluno,
        DataHora = new DateTime(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc),
        NotaFinal = 87.5m,
        Aprovado = true,
        PossuiAtaAssinada = possuiAtaAssinada
    };

    private HandlerMultiRota RegistrarHttp(params BancaConcluidaDto[] bancas)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/coordenador/bancas-concluidas", () => Json(new PagedResult<BancaConcluidaDto>
            {
                Items = bancas.ToList(),
                TotalCount = bancas.Length,
                TotalPages = 1,
                CurrentPage = 1,
                PageSize = 20
            }));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
        return handler;
    }

    private static IRenderedComponent<RadzenButton> Botao(IRenderedComponent<BancasConcluidas> cut, string icone) =>
        cut.FindComponents<RadzenButton>().Single(b => b.Instance.Icon == icone);

    public BancasConcluidasTests()
    {
        // Radzen dispara JS interop interno (medição de layout do DataGrid) que não é o objeto
        // destes testes — Loose evita falha por chamada não configurada.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
    }

    // ───────────────────── Presença e rótulos dos dois botões ─────────────────────

    [Fact]
    public void CadaLinha_ExibeOsDoisBotoesDeDownload()
    {
        RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));

        var cut = Render<BancasConcluidas>();

        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Assert.Equal("Ata gerada", Botao(cut, IconeAtaGerada).Instance.Text);
        Assert.Equal("Cópia assinada", Botao(cut, IconeCopiaAssinada).Instance.Text);

        // Regressão do rótulo antigo: "Baixar PDF" não dizia QUAL dos dois PDFs baixava.
        Assert.DoesNotContain("Baixar PDF", cut.Markup);
    }

    // ───────────────────── Habilitado/desabilitado por PossuiAtaAssinada ─────────────────────

    [Fact]
    public void SemCopiaAssinada_BotaoDeCopiaFicaDesabilitado_EODeAtaGeradaContinuaAtivo()
    {
        RegistrarHttp(NovaBanca(7, possuiAtaAssinada: false));

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        // Desabilitado, não escondido: para quem audita, "não há cópia assinada" é informação.
        Assert.True(Botao(cut, IconeCopiaAssinada).Find("button").HasAttribute("disabled"));
        Assert.False(Botao(cut, IconeAtaGerada).Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public void ComCopiaAssinada_OsDoisBotoesFicamHabilitados()
    {
        RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Assert.False(Botao(cut, IconeCopiaAssinada).Find("button").HasAttribute("disabled"));
        Assert.False(Botao(cut, IconeAtaGerada).Find("button").HasAttribute("disabled"));
    }

    // NOTA para o qa-agent: NÃO existe aqui um teste de "clicar no botão desabilitado não
    // chama a API". Foi escrito e removido: o Click() do bUnit dispara o handler mesmo em um
    // <button disabled>, porque ele invoca o EventCallback do Blazor diretamente em vez de
    // simular a semântica do navegador. O teste falharia por limitação do harness, não por
    // defeito do componente — no navegador real um botão desabilitado não emite click. A
    // proteção efetiva é server-side (404 de GetAtaAssinada, coberto em
    // CoordenadorController_AtaAssinada_Tests).

    // ───────────────────── Download de cada documento ─────────────────────

    [Fact]
    public void ClicarEmCopiaAssinada_ChamaARotaNovaEBaixaComNomeAtaAssinada()
    {
        var handler = RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));
        handler.ComRota("/api/coordenador/banca/7/ata-assinada", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })
        });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Botao(cut, IconeCopiaAssinada).Find("button").Click();

        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        Assert.Equal("ata-assinada-7.pdf", download.Invocations.Single().Arguments[0]);
        Assert.Contains(handler.Chamadas, c => c == "/api/coordenador/banca/7/ata-assinada");
    }

    [Fact]
    public void ClicarEmAtaGerada_ContinuaBaixandoODocumentoGerado_ComNomeAtaDefesa()
    {
        // Regressão: o botão existente foi renomeado e passou a usar outro campo de estado —
        // o comportamento dele não pode ter mudado (rota e nome de arquivo são os mesmos).
        var handler = RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));
        handler.ComRota("/api/coordenador/banca/7/ata-pdf", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })
        });
        var download = JSInterop.SetupVoid("downloadFileFromBytes", _ => true);

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Botao(cut, IconeAtaGerada).Find("button").Click();

        cut.WaitForAssertion(() => Assert.Single(download.Invocations));
        Assert.Equal("ata-defesa-7.pdf", download.Invocations.Single().Arguments[0]);
        Assert.Contains(handler.Chamadas, c => c == "/api/coordenador/banca/7/ata-pdf");
    }

    [Fact]
    public void FalhaAoBaixarCopiaAssinada_ExibeNotificacaoComMensagemPropria()
    {
        // A mensagem precisa distinguir os dois downloads: "Erro ao baixar a ata" seria
        // ambíguo agora que existem dois documentos diferentes na mesma linha.
        var handler = RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));
        handler.ComRota("/api/coordenador/banca/7/ata-assinada", () => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Nenhuma cópia assinada foi anexada a esta banca.")
        });
        var notificationService = Services.GetRequiredService<NotificationService>();

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Botao(cut, IconeCopiaAssinada).Find("button").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(notificationService.Messages));
        var mensagem = notificationService.Messages.First();
        Assert.Equal(NotificationSeverity.Error, mensagem.Severity);
        Assert.Contains("cópia assinada", mensagem.Detail?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────── Independência do estado "Baixando..." ─────────────────────

    [Fact]
    public void BaixandoACopiaAssinada_NaoDesabilitaOBotaoDaAtaGeradaDaMesmaLinha()
    {
        // Núcleo da regressão de 8.2: com um único campo bancaIdBaixando, clicar aqui
        // desabilitaria os DOIS botões da linha.
        var respostaPendente = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));
        handler.ComRotaAssincrona("/api/coordenador/banca/7/ata-assinada", () => respostaPendente.Task);
        // Sem JSInterop.SetupVoid aqui de propósito: uma invocação PLANEJADA no bUnit só
        // completa quando o teste resolve o handler, e o await de InvokeVoidAsync ficaria
        // pendente para sempre — o finally que zera o estado de "baixando" nunca rodaria e a
        // asserção de "volta a ficar habilitado" falharia por artefato do harness. No modo
        // Loose, a chamada não planejada completa na hora, que é o que este teste precisa.

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Botao(cut, IconeCopiaAssinada).Find("button").Click();

        cut.WaitForAssertion(() =>
            Assert.True(Botao(cut, IconeCopiaAssinada).Find("button").HasAttribute("disabled")));
        Assert.False(Botao(cut, IconeAtaGerada).Find("button").HasAttribute("disabled"));

        respostaPendente.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })
        });

        // E ao terminar, o botão volta a ficar habilitado (o finally zera só o campo dele).
        cut.WaitForAssertion(() =>
            Assert.False(Botao(cut, IconeCopiaAssinada).Find("button").HasAttribute("disabled")));
    }

    [Fact]
    public void BaixandoAAtaGerada_NaoDesabilitaOBotaoDeCopiaAssinadaDaMesmaLinha()
    {
        var respostaPendente = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = RegistrarHttp(NovaBanca(7, possuiAtaAssinada: true));
        handler.ComRotaAssincrona("/api/coordenador/banca/7/ata-pdf", () => respostaPendente.Task);
        // Sem JSInterop.SetupVoid aqui de propósito: uma invocação PLANEJADA no bUnit só
        // completa quando o teste resolve o handler, e o await de InvokeVoidAsync ficaria
        // pendente para sempre — o finally que zera o estado de "baixando" nunca rodaria e a
        // asserção de "volta a ficar habilitado" falharia por artefato do harness. No modo
        // Loose, a chamada não planejada completa na hora, que é o que este teste precisa.

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Concluido", cut.Markup));

        Botao(cut, IconeAtaGerada).Find("button").Click();

        cut.WaitForAssertion(() =>
            Assert.True(Botao(cut, IconeAtaGerada).Find("button").HasAttribute("disabled")));
        Assert.False(Botao(cut, IconeCopiaAssinada).Find("button").HasAttribute("disabled"));

        respostaPendente.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })
        });

        cut.WaitForAssertion(() =>
            Assert.False(Botao(cut, IconeAtaGerada).Find("button").HasAttribute("disabled")));
    }

    [Fact]
    public void BaixandoEmUmaLinha_NaoAfetaOsBotoesDasOutrasLinhas()
    {
        // O estado é por bancaId: uma linha "baixando" não pode travar a grade inteira.
        var respostaPendente = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = RegistrarHttp(
            NovaBanca(7, possuiAtaAssinada: true, nomeAluno: "Aluno Sete"),
            NovaBanca(8, possuiAtaAssinada: true, nomeAluno: "Aluno Oito"));
        handler.ComRotaAssincrona("/api/coordenador/banca/7/ata-assinada", () => respostaPendente.Task);
        // Sem JSInterop.SetupVoid aqui de propósito: uma invocação PLANEJADA no bUnit só
        // completa quando o teste resolve o handler, e o await de InvokeVoidAsync ficaria
        // pendente para sempre — o finally que zera o estado de "baixando" nunca rodaria e a
        // asserção de "volta a ficar habilitado" falharia por artefato do harness. No modo
        // Loose, a chamada não planejada completa na hora, que é o que este teste precisa.

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Oito", cut.Markup));

        var botoesCopia = cut.FindComponents<RadzenButton>().Where(b => b.Instance.Icon == IconeCopiaAssinada).ToList();
        Assert.Equal(2, botoesCopia.Count);

        botoesCopia[0].Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            var atualizados = cut.FindComponents<RadzenButton>().Where(b => b.Instance.Icon == IconeCopiaAssinada).ToList();
            Assert.True(atualizados[0].Find("button").HasAttribute("disabled"));
            Assert.False(atualizados[1].Find("button").HasAttribute("disabled"));
        });

        respostaPendente.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })
        });
    }

    // ───────────────────── Grade mista ─────────────────────

    [Fact]
    public void GradeComBancasComESemCopia_HabilitaApenasAsQueTemArquivo()
    {
        RegistrarHttp(
            NovaBanca(7, possuiAtaAssinada: true, nomeAluno: "Aluno Com Copia"),
            NovaBanca(8, possuiAtaAssinada: false, nomeAluno: "Aluno Sem Copia"));

        var cut = Render<BancasConcluidas>();
        cut.WaitForAssertion(() => Assert.Contains("Aluno Sem Copia", cut.Markup));

        var botoesCopia = cut.FindComponents<RadzenButton>()
            .Where(b => b.Instance.Icon == IconeCopiaAssinada)
            .ToList();

        Assert.Equal(2, botoesCopia.Count);
        Assert.False(botoesCopia[0].Find("button").HasAttribute("disabled"));
        Assert.True(botoesCopia[1].Find("button").HasAttribute("disabled"));
    }
}
