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
/// Issue #81 (D13) — tela do Aluno. Testes bUnit (componente renderizado de verdade), ao lado do
/// arquivo <see cref="MeuTccTests"/>, que cobre por reflection a lógica pura de
/// <c>AlternarFeedback</c>/<c>FoiReprovadoNaBanca</c> e continua válido (nenhum dos dois métodos
/// mudou nesta issue).
///
/// O que se testa aqui é a materialização visível da reabertura do ciclo — a asserção de maior
/// valor do front nesta issue (seção 14, item 6 da arquitetura): o formulário de upload some
/// enquanto existe uma Final NÃO rejeitada e VOLTA quando a Final foi rejeitada. O gate na página
/// (<c>Any(e =&gt; e.Tipo == Final &amp;&amp; e.Status != Rejeitada)</c>) tem que espelhar o
/// pre-check de <c>TccController.EnviarEntrega</c> (D6) e o filtro do índice único (D2); se
/// divergir, o aluno vê um formulário que o backend recusa (ou não vê o formulário que o backend
/// aceitaria).
/// </summary>
public class MeuTccGateDeUploadTests : BunitContext
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

    public MeuTccGateDeUploadTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    private static Tcc TccCom(StatusTcc status) => new()
    {
        Id = 1,
        Titulo = "TCC de Teste",
        Resumo = "Resumo do trabalho.",
        Status = status,
        DataCriacao = DateTime.UtcNow.AddMonths(-3)
    };

    private static Entrega NovaEntrega(int id, TipoEntrega tipo, StatusEntrega status, string? feedback = null, int diasAtras = 0) => new()
    {
        Id = id,
        TccId = 1,
        Titulo = $"Entrega {id}",
        ArquivoCaminho = "/x.pdf",
        Tipo = tipo,
        Status = status,
        Feedback = feedback,
        DataEnvio = DateTime.UtcNow.AddDays(-diasAtras)
    };

    /// <summary>
    /// Issue #82 (seção 12, item 7): o status padrão do TCC semeado passou de <c>Aprovado</c> para
    /// <c>EmAndamento</c>. Todos os cenários deste arquivo têm PELO MENOS UMA entrega, e
    /// "Aprovado com entregas" virou um estado impossível depois desta issue (invariante:
    /// <c>Aprovado ⟺ zero Entregas</c>). Além de deixar o seed verdadeiro, isso converte o
    /// arquivo inteiro em tripwire do Grupo B: as guardas <c>MeuTcc.razor:54</c> (aba de
    /// entregas) e <c>:399</c> (disparo de <c>CarregarEntregas</c>) comparavam por igualdade
    /// exata com <c>Aprovado</c> antes do fix — com <c>EmAndamento</c>, nem o formulário de
    /// upload nem o histórico renderizariam e todos os testes abaixo ficariam vermelhos.
    /// </summary>
    private HandlerMultiRota RegistrarHttp(params Entrega[] entregas)
        => RegistrarHttp(StatusTcc.EmAndamento, entregas);

    private HandlerMultiRota RegistrarHttp(StatusTcc statusTcc, params Entrega[] entregas)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/tcc/meu-tcc", () => Json(TccCom(statusTcc)))
            .ComRota("/api/tcc/entregas", () => Json(new PagedResult<Entrega>
            {
                Items = entregas.OrderByDescending(e => e.DataEnvio).ToList(),
                TotalCount = entregas.Length,
                TotalPages = 1,
                CurrentPage = 1,
                PageSize = 100
            }))
            .ComRota("/api/tcc/acompanhamentos", () => Json(new List<Acompanhamento>()))
            .ComRota("/api/tcc/minha-banca", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });

        return handler;
    }

    // ── Gate de upload (D13) ─────────────────────────────────────────────────────────────

    [Fact]
    public void SemEntregaFinal_ExibeOFormularioDeNovaEntrega()
    {
        RegistrarHttp(NovaEntrega(1, TipoEntrega.Parcial, StatusEntrega.Aprovada));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Nova Entrega", cut.Markup));
        Assert.DoesNotContain("Trabalho Concluído!", cut.Markup);
    }

    [Fact]
    public void FinalPendenteDeVeredito_EscondeOFormulario_EExibeTrabalhoConcluido()
    {
        RegistrarHttp(NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Pendente));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Trabalho Concluído!", cut.Markup));
        Assert.DoesNotContain("Nova Entrega", cut.Markup);
    }

    [Fact]
    public void FinalAprovada_EscondeOFormulario()
    {
        // Ciclo encerrado de vez: é o único estado terminal do lado do aluno.
        RegistrarHttp(NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Aprovada));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Trabalho Concluído!", cut.Markup));
        Assert.DoesNotContain("Nova Entrega", cut.Markup);
    }

    [Fact]
    public void FinalRejeitada_ExibeOFormularioDeNovoEOAlertaComOParecerDoOrientador()
    {
        // O núcleo visível da issue: com a Final rejeitada, o aluno volta a poder enviar e
        // entende POR QUE o formulário reapareceu sem depender só do e-mail.
        RegistrarHttp(NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Refazer a análise dos resultados."));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Sua versão final foi rejeitada", cut.Markup));
        Assert.Contains("Nova Entrega", cut.Markup);
        Assert.Contains("Refazer a análise dos resultados.", cut.Markup);
        Assert.DoesNotContain("Trabalho Concluído!", cut.Markup);
    }

    [Fact]
    public void FinalRejeitadaSemParecer_AindaAssimExibeOFormularioEOAlerta()
    {
        // Feedback vazio não deveria ocorrer (o motivo é obrigatório no backend), mas a tela
        // não pode depender disso para reabrir o formulário.
        RegistrarHttp(NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: null));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Sua versão final foi rejeitada", cut.Markup));
        Assert.Contains("Nova Entrega", cut.Markup);
        Assert.DoesNotContain("Parecer do orientador:", cut.Markup);
    }

    [Fact]
    public void FinalRejeitadaSeguidaDeNovaFinalPendente_VoltaAEsconderOFormulario()
    {
        // Depois do reenvio o gate volta a valer — o alerta de rejeição não pode "grudar" na
        // tela por causa da linha antiga.
        RegistrarHttp(
            NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Corrigir o capitulo 4.", diasAtras: 5),
            NovaEntrega(3, TipoEntrega.Final, StatusEntrega.Pendente, diasAtras: 0));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Trabalho Concluído!", cut.Markup));
        Assert.DoesNotContain("Nova Entrega", cut.Markup);
        Assert.DoesNotContain("Sua versão final foi rejeitada", cut.Markup);
    }

    // ── Badge de veredito no histórico de entregas (D13) ──────────────────────────────────

    [Fact]
    public void HistoricoDeEntregas_ExibeOBadgeDeVeredictoDeCadaEntrega()
    {
        // Substitui a heurística antiga ("tem Feedback preenchido" => Avaliado): o badge passa a
        // refletir Entrega.Status diretamente.
        RegistrarHttp(
            NovaEntrega(1, TipoEntrega.Parcial, StatusEntrega.Aprovada, diasAtras: 20),
            NovaEntrega(2, TipoEntrega.Parcial, StatusEntrega.Rejeitada, feedback: "Refazer.", diasAtras: 10),
            NovaEntrega(3, TipoEntrega.Parcial, StatusEntrega.Pendente, diasAtras: 1));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Aguardando Veredito", cut.Markup));
        Assert.Contains("Aprovada", cut.Markup);
        Assert.Contains("Rejeitada", cut.Markup);
    }

    [Fact]
    public void EntregaComFeedbackMasSemVeredicto_ContinuaMarcadaComoAguardandoVeredito()
    {
        // Ter parecer/nota registrados via "Avaliar" NÃO é veredito (D3/5.2): são duas
        // interações distintas sobre a mesma entrega.
        RegistrarHttp(NovaEntrega(1, TipoEntrega.Parcial, StatusEntrega.Pendente, feedback: "Comentários gerais."));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Aguardando Veredito", cut.Markup));
        Assert.Contains("Ler Feedback", cut.Markup);
    }

    // ── Grupo B da issue #82: a aba de entregas nos DOIS estados ativos ───────────────────

    [Fact]
    public void TccEmAndamento_RenderizaAAbaDeEntregasEBuscaOHistorico()
    {
        // B2 + B3 explicitados. Antes do fix, um TCC em EmAndamento nem entrava no bloco da aba
        // (B2) e nem disparava CarregarEntregas (B3) — a tela ficaria no "Carregando histórico..."
        // eterno descrito em 5.1 da arquitetura.
        var handler = RegistrarHttp(
            StatusTcc.EmAndamento,
            NovaEntrega(1, TipoEntrega.Parcial, StatusEntrega.Aprovada));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Nova Entrega", cut.Markup));
        Assert.Contains("Histórico de Entregas", cut.Markup);
        Assert.DoesNotContain("Carregando histórico...", cut.Markup);
        Assert.Contains(handler.Chamadas, c => c.StartsWith("/api/tcc/entregas", StringComparison.Ordinal));
    }

    [Fact]
    public void TccAprovadoSemNenhumaEntrega_RenderizaAAbaDeEntregasComHistoricoVazio()
    {
        // O outro lado da guarda: Aprovado (orientador designado, zero entregas) continua
        // exibindo o formulário de upload — é por ele que a PRIMEIRA entrega é enviada, e é ela
        // que dispara a transição para EmAndamento no backend (D2).
        var handler = RegistrarHttp(StatusTcc.Aprovado);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Nova Entrega", cut.Markup));
        Assert.DoesNotContain("Carregando histórico...", cut.Markup);
        Assert.DoesNotContain("Trabalho Concluído!", cut.Markup);
        Assert.Contains(handler.Chamadas, c => c.StartsWith("/api/tcc/entregas", StringComparison.Ordinal));
    }

    [Fact]
    public void TccEmAndamentoComFinalRejeitada_ContinuaReabrindoOFormulario()
    {
        // Interação #81 × #82: a rejeição da Final não mexe em Tcc.Status (o TCC segue em
        // EmAndamento, que é a leitura verdadeira — o aluno já enviou algo), e o ciclo reaberto
        // precisa continuar funcionando nesse estado.
        RegistrarHttp(
            StatusTcc.EmAndamento,
            NovaEntrega(2, TipoEntrega.Final, StatusEntrega.Rejeitada, feedback: "Refazer o capítulo 4."));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Sua versão final foi rejeitada", cut.Markup));
        Assert.Contains("Nova Entrega", cut.Markup);
    }
}
