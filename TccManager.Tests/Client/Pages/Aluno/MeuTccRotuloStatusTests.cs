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
/// Issue #82 (D8.3 e D9) — tela do Aluno: o badge de situação e o texto de apoio passam a
/// distinguir os dois estados agora reais.
///
/// Estes são os testes que travam o SINTOMA relatado no teste manual que originou a issue: badge
/// dizendo "Aprovado" ao lado de uma frase fixa dizendo "seu TCC está em andamento!". Depois desta
/// issue o badge vem de <c>StatusTccFormatter</c> (nunca mais o <c>ToString()</c> cru do enum) e o
/// alerta é um por estado.
///
/// Complementa <see cref="MeuTccGateDeUploadTests"/> (gate de upload/histórico, Grupo B) e
/// <see cref="MeuTccTests"/> (lógica pura por reflection).
/// </summary>
public class MeuTccRotuloStatusTests : BunitContext
{
    private sealed class HandlerMultiRota : HttpMessageHandler
    {
        private readonly List<(string Prefixo, Func<HttpResponseMessage> Resposta)> _respostas = new();

        public HandlerMultiRota ComRota(string prefixo, Func<HttpResponseMessage> resposta)
        {
            _respostas.Add((prefixo, resposta));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var caminho = request.RequestUri!.PathAndQuery;

            foreach (var (prefixo, resposta) in _respostas)
            {
                if (caminho.StartsWith(prefixo, StringComparison.Ordinal))
                    return Task.FromResult(resposta());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json<T>(T valor) => new(HttpStatusCode.OK) { Content = JsonContent.Create(valor) };

    public MeuTccRotuloStatusTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    private void RegistrarHttp(StatusTcc status, params Entrega[] entregas)
    {
        var handler = new HandlerMultiRota()
            .ComRota("/api/tcc/meu-tcc", () => Json(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo do trabalho.",
                Status = status,
                DataCriacao = DateTime.UtcNow.AddMonths(-3)
            }))
            .ComRota("/api/tcc/entregas", () => Json(new PagedResult<Entrega>
            {
                Items = entregas.ToList(),
                TotalCount = entregas.Length,
                TotalPages = 1,
                CurrentPage = 1,
                PageSize = 100
            }))
            .ComRota("/api/tcc/acompanhamentos", () => Json(new List<Acompanhamento>()))
            .ComRota("/api/tcc/minha-banca", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
    }

    private static Entrega NovaEntrega(int id) => new()
    {
        Id = id,
        TccId = 1,
        Titulo = $"Entrega {id}",
        ArquivoCaminho = "/x.pdf",
        Tipo = TipoEntrega.Parcial,
        Status = StatusEntrega.Pendente,
        DataEnvio = DateTime.UtcNow.AddDays(-id)
    };

    // ── D8.3: badge de situação ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StatusTcc.Pendente, "Em análise")]
    [InlineData(StatusTcc.Aprovado, "Aguardando 1ª entrega")]
    [InlineData(StatusTcc.EmAndamento, "Em andamento")]
    [InlineData(StatusTcc.AguardandoDefesa, "Aguardando defesa")]
    [InlineData(StatusTcc.Finalizado, "Finalizado")]
    public void Badge_ExibeORotuloAmigavelDoStatus(StatusTcc status, string rotuloEsperado)
    {
        RegistrarHttp(status, NovaEntrega(1));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Situação da Proposta", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(rotuloEsperado, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusTcc.EmAndamento)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    public void Badge_NuncaExibeONomeGrudadoDoEnum(StatusTcc status)
    {
        // "EmAndamento"/"AguardandoDefesa" apareciam exatamente assim no badge antes desta issue.
        RegistrarHttp(status, NovaEntrega(1));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Situação da Proposta", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(status.ToString(), cut.Markup, StringComparison.Ordinal);
    }

    // ── D9: o alerta de apoio, um por estado ──────────────────────────────────────────────

    [Fact]
    public void TccAprovado_ExibeOAlertaQuePedeAPrimeiraEntrega()
    {
        RegistrarHttp(StatusTcc.Aprovado);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains(
            "Envie sua primeira entrega abaixo para dar início ao acompanhamento.",
            cut.Markup,
            StringComparison.Ordinal));
        Assert.Contains("Sua proposta foi aprovada", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Seu TCC está em andamento.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TccEmAndamento_ExibeOAlertaDeAcompanhamentoEmCurso()
    {
        RegistrarHttp(StatusTcc.EmAndamento, NovaEntrega(1));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains(
            "Seu TCC está em andamento.",
            cut.Markup,
            StringComparison.Ordinal));
        Assert.Contains(
            "Continue enviando suas entregas para acompanhamento do orientador.",
            cut.Markup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Envie sua primeira entrega", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusTcc.Aprovado)]
    [InlineData(StatusTcc.EmAndamento)]
    public void OAlertaAntigoQueMisturavaOsDoisEstados_NaoExisteMais(StatusTcc status)
    {
        // Regressão direta do sintoma reportado: a frase fixa "Sua proposta foi aprovada e seu TCC
        // está em andamento!" era exibida para TODO TCC aprovado, inclusive um que não tinha
        // enviado nada. Com dois estados reais, ela seria falsa metade do tempo.
        RegistrarHttp(status, NovaEntrega(1));

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains("Situação da Proposta", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(
            "Sua proposta foi aprovada e seu TCC está em andamento!",
            cut.Markup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TccPendente_MantemOAlertaDeAnaliseSemNenhumDosDoisTextosNovos()
    {
        // Aresta oposta: nada do que D9 introduziu pode vazar para o estado Pendente.
        RegistrarHttp(StatusTcc.Pendente);

        var cut = Render<MeuTcc>();

        cut.WaitForAssertion(() => Assert.Contains(
            "Sua proposta está sendo analisada.", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Envie sua primeira entrega", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Seu TCC está em andamento.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Nova Entrega", cut.Markup, StringComparison.Ordinal);
    }
}
