using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Pages.Professor;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Client.Pages.Professor;

/// <summary>
/// Issue #82 (D8.3) — tela do Professor: o badge "Status: ..." passa a usar
/// <c>StatusTccFormatter</c> em vez da interpolação direta do enum.
///
/// É o segundo (e último) ponto de consumo do formatador. Sem ele, o orientador veria
/// "Status: EmAndamento" grudado assim que o valor passou a ser realmente atribuído pelo gatilho
/// automático (D2) — vocabulário diferente do da tela do aluno para o mesmo TCC.
///
/// O Grupo A desta página (linhas 42 e 104: os controles de veredito e o "Dar Aceite Final" já
/// guardados por <c>Aprovado || EmAndamento</c> desde a #81) NÃO foi tocado por esta issue e já é
/// coberto por <see cref="DetalhesTccVeredictoTests"/>.
/// </summary>
public class DetalhesTccRotuloStatusTests : BunitContext
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

    public DetalhesTccRotuloStatusTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton<DialogService>();
    }

    private void RegistrarHttp(StatusTcc status)
    {
        var tcc = new Tcc
        {
            Id = 1,
            Titulo = "TCC de Teste",
            Resumo = "Resumo do trabalho.",
            Status = status,
            DataCriacao = DateTime.UtcNow.AddMonths(-3),
            Aluno = new Usuario { Id = 10, Nome = "Aluno Orientando", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno },
            AlunoId = 10,
            OrientadorId = 20,
            Entregas = new List<Entrega>()
        };

        var handler = new HandlerMultiRota().ComRota("/api/orientador/tcc/1", () => Json(tcc));

        Services.AddScoped(_ => new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") });
    }

    private IRenderedComponent<DetalhesTcc> RenderizarPagina()
        => Render<DetalhesTcc>(parametros => parametros.Add(p => p.TccId, 1));

    [Theory]
    [InlineData(StatusTcc.Aprovado, "Status: Aguardando 1ª entrega")]
    [InlineData(StatusTcc.EmAndamento, "Status: Em andamento")]
    [InlineData(StatusTcc.AguardandoDefesa, "Status: Aguardando defesa")]
    [InlineData(StatusTcc.Finalizado, "Status: Finalizado")]
    public void Badge_ExibeORotuloAmigavelDoStatus(StatusTcc status, string textoEsperado)
    {
        RegistrarHttp(status);

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("TCC de Teste", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(textoEsperado, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StatusTcc.EmAndamento)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    public void Badge_NuncaExibeONomeGrudadoDoEnum(StatusTcc status)
    {
        RegistrarHttp(status);

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("TCC de Teste", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(status.ToString(), cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Badge_DistingueAprovadoDeEmAndamentoParaOOrientador()
    {
        // O orientador precisa conseguir separar, na lista dele, quem já começou de quem ainda
        // não enviou nada — é o objetivo declarado pelo usuário na issue.
        RegistrarHttp(StatusTcc.Aprovado);

        var cut = RenderizarPagina();

        cut.WaitForAssertion(() => Assert.Contains("TCC de Teste", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Aguardando 1ª entrega", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Status: Em andamento", cut.Markup, StringComparison.Ordinal);
    }
}
