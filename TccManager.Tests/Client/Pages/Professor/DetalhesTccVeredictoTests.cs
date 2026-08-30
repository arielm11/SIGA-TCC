using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using TccManager.Client.Pages.Professor;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Client.Pages.Professor;

/// <summary>
/// Issue #81 (D12) — controles de veredito por entrega na tela do Professor. Testes bUnit
/// (componente renderizado), complementares ao arquivo <see cref="DetalhesTccTests"/>, que cobre
/// por reflection a lógica pura de feedback/acompanhamento e continua válido.
///
/// O que estes testes travam:
/// - o badge por entrega reflete <c>Entrega.Status</c> (não a heurística antiga de "tem Feedback");
/// - os botões Aprovar/Rejeitar respeitam D8 (somem para uma Final já Rejeitada, que é terminal) e
///   D9 (somem com o TCC fora de Aprovado/EmAndamento);
/// - "Dar Aceite Final" só habilita com uma Final APROVADA (D7), com legenda distinta para cada
///   um dos três casos de bloqueio;
/// - a rejeição reusa <c>RejeitarPropostaDialog</c> parametrizado e faz POST na rota do veredito.
/// </summary>
public class DetalhesTccVeredictoTests : BunitContext
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

    public DetalhesTccVeredictoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    private static Entrega NovaEntrega(int id, string titulo, TipoEntrega tipo, StatusEntrega status, string? feedback = null, int diasAtras = 0) => new()
    {
        Id = id,
        TccId = 1,
        Titulo = titulo,
        ArquivoCaminho = "/x.pdf",
        Tipo = tipo,
        Status = status,
        Feedback = feedback,
        DataEnvio = DateTime.UtcNow.AddDays(-diasAtras)
    };

    private static Tcc TccCom(StatusTcc status, params Entrega[] entregas) => new()
    {
        Id = 1,
        Titulo = "TCC de Teste",
        Resumo = "Resumo do trabalho.",
        Status = status,
        DataCriacao = DateTime.UtcNow.AddMonths(-3),
        Aluno = new Usuario { Id = 10, Nome = "Aluno Orientando", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno },
        AlunoId = 10,
        OrientadorId = 20,
        Entregas = entregas.OrderByDescending(e => e.DataEnvio).ToList()
    };

    private HandlerMultiRota RegistrarHttp(Tcc tcc, Func<HttpResponseMessage>? respostaDoVeredito = null)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/orientador/entregas/", respostaDoVeredito ?? (() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Entrega final rejeitada. O aluno já pode enviar uma nova versão.")
            }))
            .ComRota("/api/orientador/tcc/1", () => Json(tcc));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

    private IRenderedComponent<DetalhesTcc> RenderizarPagina()
        => Render<DetalhesTcc>(parametros => parametros.Add(p => p.TccId, 1));

    /// <summary>
    /// Host com <c>&lt;RadzenDialog/&gt;</c> ao lado da página, para os fluxos que abrem modal
    /// (mesmo padrão do teste de rejeição da tela do Coordenador).
    /// </summary>
    private static readonly RenderFragment HostComDialogo = builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<DetalhesTcc>(1);
        builder.AddComponentParameter(2, nameof(DetalhesTcc.TccId), 1);
        builder.CloseComponent();
    };

    private static IElement BotaoComTexto<TComponent>(IRenderedComponent<TComponent> cut, string texto)
        where TComponent : IComponent
        => cut.FindAll("button").Single(b => b.TextContent.Contains(texto, StringComparison.Ordinal));

    private static bool TemBotaoComTexto<TComponent>(IRenderedComponent<TComponent> cut, string texto)
        where TComponent : IComponent
        => cut.FindAll("button").Any(b => b.TextContent.Contains(texto, StringComparison.Ordinal));

    // ── Badge de veredito por entrega ─────────────────────────────────────────────────────

    [Fact]
    public void Badges_RefletemOStatusDeCadaEntrega()
    {
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Capítulo 1", TipoEntrega.Parcial, StatusEntrega.Aprovada, diasAtras: 30),
            NovaEntrega(2, "Capítulo 2", TipoEntrega.Parcial, StatusEntrega.Rejeitada, feedback: "Refazer.", diasAtras: 20),
            NovaEntrega(3, "Capítulo 3", TipoEntrega.Parcial, StatusEntrega.Pendente, diasAtras: 10)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Capítulo 1", cut.Markup));
        Assert.Contains("Aguardando Veredito", cut.Markup);
        Assert.Contains("Aprovada", cut.Markup);
        Assert.Contains("Rejeitada", cut.Markup);
    }

    // ── Botões de veredito: D8 e D9 ───────────────────────────────────────────────────────

    [Fact]
    public void EntregaPendente_ExibeOsBotoesAprovarERejeitar()
    {
        RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));
        Assert.True(TemBotaoComTexto(cut, "Aprovar"));
        Assert.True(TemBotaoComTexto(cut, "Rejeitar"));
    }

    [Fact]
    public void FinalJaRejeitada_NaoExibeMaisOsBotoesDeVeredicto_MasMantemOBadge()
    {
        // D8: a linha é terminal — o próximo passo é o aluno enviar uma nova Final, não o
        // professor mudar de ideia sobre esta.
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Refazer a análise.")));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));
        Assert.Contains("Rejeitada", cut.Markup);
        Assert.False(TemBotaoComTexto(cut, "Aprovar"));
        Assert.False(TemBotaoComTexto(cut, "Rejeitar"));
    }

    [Fact]
    public void ParcialJaRejeitada_ContinuaExibindoOsBotoesDeVeredicto()
    {
        // Assimetria deliberada em relação ao caso acima: só a Final é terminal (é ela que
        // participa do índice único filtrado).
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Capítulo 1", TipoEntrega.Parcial, StatusEntrega.Rejeitada, feedback: "Refazer.")));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Capítulo 1", cut.Markup));
        Assert.True(TemBotaoComTexto(cut, "Aprovar"));
        Assert.True(TemBotaoComTexto(cut, "Rejeitar"));
    }

    [Fact]
    public void TccAguardandoDefesa_NaoExibeNenhumBotaoDeVeredicto()
    {
        // D9 na UI: depois do aceite final o veredito não é mais alterável (o backend devolve
        // 400; a tela nem oferece a ação).
        RegistrarHttp(TccCom(
            StatusTcc.AguardandoDefesa,
            NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Aprovada)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Aceite Final Concedido", cut.Markup));
        Assert.False(TemBotaoComTexto(cut, "Aprovar"));
        Assert.False(TemBotaoComTexto(cut, "Rejeitar"));
    }

    // ── D7: "Dar Aceite Final" exige Final aprovada ───────────────────────────────────────

    [Fact]
    public void DarAceiteFinal_SemNenhumaEntregaFinal_FicaDesabilitadoComLegendaDeEnvioPendente()
    {
        RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(1, "Capítulo 1", TipoEntrega.Parcial, StatusEntrega.Aprovada)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Dar Aceite Final", cut.Markup));
        Assert.True(BotaoComTexto(cut, "Dar Aceite Final").HasAttribute("disabled"));
        Assert.Contains("Aguardando envio da Versão Final pelo aluno.", cut.Markup);
    }

    [Fact]
    public void DarAceiteFinal_ComFinalPendenteDeVeredicto_FicaDesabilitadoComLegendaDeAvaliacaoPendente()
    {
        RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Dar Aceite Final", cut.Markup));
        Assert.True(BotaoComTexto(cut, "Dar Aceite Final").HasAttribute("disabled"));
        Assert.Contains("A versão final ainda não foi avaliada.", cut.Markup);
    }

    [Fact]
    public void DarAceiteFinal_ComFinalRejeitada_FicaDesabilitadoComLegendaDeNovoEnvio()
    {
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Refazer.")));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Dar Aceite Final", cut.Markup));
        Assert.True(BotaoComTexto(cut, "Dar Aceite Final").HasAttribute("disabled"));
        Assert.Contains("A versão final foi rejeitada. Aguarde o novo envio do aluno.", cut.Markup);
    }

    [Fact]
    public void DarAceiteFinal_ComFinalAprovada_FicaHabilitadoESemLegendaDeBloqueio()
    {
        RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Aprovada)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Dar Aceite Final", cut.Markup));
        Assert.False(BotaoComTexto(cut, "Dar Aceite Final").HasAttribute("disabled"));
        Assert.DoesNotContain("A versão final ainda não foi avaliada.", cut.Markup);
        Assert.DoesNotContain("A versão final foi rejeitada.", cut.Markup);
        Assert.DoesNotContain("Aguardando envio da Versão Final pelo aluno.", cut.Markup);
    }

    [Fact]
    public void DarAceiteFinal_ComFinalRejeitadaEOutraFinalAprovada_FicaHabilitado()
    {
        // Depois do reenvio + aprovação, a linha rejeitada antiga continua na tela mas não pode
        // mais bloquear o aceite.
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Refazer.", diasAtras: 10),
            NovaEntrega(2, "Versão Final Corrigida", TipoEntrega.Final, StatusEntrega.Aprovada, diasAtras: 1)));

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("Versão Final Corrigida", cut.Markup));
        Assert.False(BotaoComTexto(cut, "Dar Aceite Final").HasAttribute("disabled"));
    }

    // ── Fluxo de aprovação (confirmação antes do POST) ───────────────────────────────────

    [Fact]
    public void ClicarAprovar_ConfirmandoNoDialogo_EnviaPostParaARotaDeAprovacao()
    {
        var handler = RegistrarHttp(
            TccCom(StatusTcc.EmAndamento, NovaEntrega(5, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)),
            respostaDoVeredito: () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Entrega aprovada.")
            });

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Aprovar").Click();
        cut.WaitForAssertion(() => Assert.Contains("Tem certeza que deseja aprovar esta entrega?", cut.Markup));

        // A partir daqui existem dois botões "Aprovar" (o da entrega e o de confirmação do
        // modal) — o clique tem que ser no do modal.
        cut.FindAll(".rz-dialog button").Single(b => b.TextContent.Contains("Aprovar", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Requisicoes, r => r.Caminho == "/api/orientador/entregas/5/aprovar"));

        var post = handler.Requisicoes.Single(r => r.Caminho == "/api/orientador/entregas/5/aprovar");
        Assert.Equal(HttpMethod.Post, post.Metodo);
        // Endpoint sem corpo (D3): o veredito de aprovação não carrega payload nenhum.
        Assert.Equal(string.Empty, post.Corpo);

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Success));
    }

    [Fact]
    public void ClicarAprovar_CancelandoAConfirmacao_NaoEnviaNenhumaRequisicao()
    {
        var handler = RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(5, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)));

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Aprovar").Click();
        cut.WaitForAssertion(() => Assert.Contains("Tem certeza que deseja aprovar esta entrega?", cut.Markup));

        cut.FindAll(".rz-dialog button").Single(b => b.TextContent.Contains("Cancelar", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Tem certeza que deseja aprovar esta entrega?", cut.Markup));
        Assert.DoesNotContain(handler.Requisicoes, r => r.Caminho.Contains("/aprovar", StringComparison.Ordinal));
    }

    // ── Fluxo de rejeição (reuso do RejeitarPropostaDialog parametrizado) ─────────────────

    [Fact]
    public void ClicarRejeitar_AbreODialogoComOsRotulosDeEntregaEOParecerAtualPreCarregado()
    {
        // D4: rejeitar sobrescreve o Feedback — o professor precisa ver o que está substituindo.
        RegistrarHttp(TccCom(
            StatusTcc.EmAndamento,
            NovaEntrega(1, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente, feedback: "Parecer anterior registrado.")));

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Rejeitar").Click();

        cut.WaitForAssertion(() => Assert.Contains("Parecer / Motivo da Rejeição", cut.Markup));
        Assert.True(TemBotaoComTexto(cut, "Rejeitar Entrega"));
        // Rótulos do chamador original (Coordenador) não podem vazar para este reuso.
        Assert.DoesNotContain("Rejeitar Proposta", cut.Markup);
        Assert.Equal("Parecer anterior registrado.", cut.Find("textarea").GetAttribute("value"));
    }

    [Fact]
    public void ConfirmarRejeicao_EnviaPostParaARotaDoVeredicto_ERecarregaATela()
    {
        var tcc = TccCom(StatusTcc.EmAndamento, NovaEntrega(5, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente));
        var handler = RegistrarHttp(tcc);

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Rejeitar").Click();
        cut.WaitForAssertion(() => Assert.Contains("Parecer / Motivo da Rejeição", cut.Markup));

        // Motivo sem acentuação de propósito: o corpo é comparado como JSON cru, e o
        // serializador do System.Text.Json escapa caracteres não-ASCII em sequências \uXXXX.
        cut.Find("textarea").Change("Faltam os resultados do capitulo 4.");
        BotaoComTexto(cut, "Rejeitar Entrega").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            handler.Requisicoes, r => r.Caminho == "/api/orientador/entregas/5/rejeitar"));

        var post = handler.Requisicoes.Single(r => r.Caminho == "/api/orientador/entregas/5/rejeitar");
        Assert.Equal(HttpMethod.Post, post.Metodo);
        Assert.Contains("Faltam os resultados do capitulo 4.", post.Corpo);

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages, m => m.Severity == NotificationSeverity.Success));
        // Recarga da tela após o sucesso (mesmo padrão das demais ações da página).
        Assert.True(handler.Chamadas.Count(c => c == "/api/orientador/tcc/1") >= 2);
    }

    [Fact]
    public void CancelarODialogoDeRejeicao_NaoEnviaNenhumaRequisicaoDeVeredicto()
    {
        var handler = RegistrarHttp(TccCom(StatusTcc.EmAndamento, NovaEntrega(5, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)));

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Rejeitar").Click();
        cut.WaitForAssertion(() => Assert.Contains("Parecer / Motivo da Rejeição", cut.Markup));

        BotaoComTexto(cut, "Cancelar").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("Parecer / Motivo da Rejeição", cut.Markup));
        Assert.DoesNotContain(handler.Requisicoes, r => r.Caminho.Contains("/rejeitar", StringComparison.Ordinal));
    }

    [Fact]
    public void FalhaNoVeredicto_ExibeAMensagemDoBackendNoToastDeErro()
    {
        // 409 de D8 (Final já rejeitada) não é alcançável pela UI porque o botão some — mas se
        // o estado da tela estiver defasado, a mensagem do backend precisa chegar ao professor.
        var handler = RegistrarHttp(
            TccCom(StatusTcc.EmAndamento, NovaEntrega(5, "Versão Final", TipoEntrega.Final, StatusEntrega.Pendente)),
            respostaDoVeredito: () => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("Esta entrega final já foi rejeitada e o ciclo foi reaberto.")
            });

        var cut = Render(HostComDialogo);
        cut.WaitForAssertion(() => Assert.Contains("Versão Final", cut.Markup));

        BotaoComTexto(cut, "Rejeitar").Click();
        cut.WaitForAssertion(() => Assert.Contains("Parecer / Motivo da Rejeição", cut.Markup));

        cut.Find("textarea").Change("Motivo qualquer.");
        BotaoComTexto(cut, "Rejeitar Entrega").Click();

        var notificationService = Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() => Assert.Contains(
            notificationService.Messages,
            m => m.Severity == NotificationSeverity.Error &&
                 m.Detail is string detalhe &&
                 detalhe.Contains("já foi rejeitada", StringComparison.Ordinal)));
    }
}
