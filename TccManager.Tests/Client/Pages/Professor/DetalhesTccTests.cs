using System.Reflection;
using TccManager.Client.Pages.Professor;
using TccManager.Shared.DTOs;
using TccManager.Shared.Models;
using TccManager.Tests.Client.Fakes;
using Xunit;

namespace TccManager.Tests.Client.Pages.Professor;

/// <summary>
/// Testes da lógica C# pura de <see cref="DetalhesTcc"/> introduzida/preservada na N3 Etapa 4.
///
/// Contexto: não há infraestrutura bUnit no projeto (gap pré-existente; RNF-04 não exige teste de UI
/// automatizado). Portanto <c>RadzenTabs</c>, <c>RadzenAccordion</c>, <c>EditForm</c>/
/// <c>DataAnnotationsValidator</c>, o gating do botão "Dar Aceite Final" (expressão no markup),
/// <c>DialogService.Confirm</c>, <c>NotificationService</c> e todos os fluxos HTTP NÃO são cobertos
/// aqui — ver docs/testes/2026-07-14-migracao-radzen-blazor-etapa4.md.
///
/// O que É coberto: os membros de <c>@code</c> que são transição de estado pura / mapeamento e não
/// dependem de <c>HttpClient</c>, <c>DialogService</c> ou <c>NotificationService</c>:
/// <c>AbrirFormFeedback</c>/<c>FecharFormFeedback</c> (form inline de feedback por entrega),
/// <c>PrepararEdicaoAcompanhamento</c>/<c>CancelarEdicaoAcompanhamento</c> (alternância criar vs.
/// editar ata) e o helper <c>UrlArquivo</c> (extraído do markup nesta etapa). São membros privados do
/// componente, acessados via reflection sobre <c>new DetalhesTcc()</c>, que não aciona renderer nem
/// injeção de dependência.
///
/// Onde há dependência injetada mas SEM I/O real, ela é preenchida por reflection com objetos
/// concretos já usados no projeto: <see cref="FakeJsRuntime"/> (apenas registra a interop cosmética
/// <c>window.scrollTo</c>) e um <see cref="HttpClient"/> com <c>BaseAddress</c> definido, do qual
/// <c>UrlArquivo</c> só lê a propriedade — nenhuma requisição HTTP é feita em nenhum teste deste arquivo.
/// </summary>
public class DetalhesTccTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void DefinirInjetado(DetalhesTcc componente, string nome, object valor)
    {
        var propriedade = typeof(DetalhesTcc).GetProperty(nome, Privados);
        if (propriedade != null)
        {
            propriedade.SetValue(componente, valor);
            return;
        }

        var campo = typeof(DetalhesTcc).GetField(nome, Privados)
            ?? throw new MissingMemberException(nameof(DetalhesTcc), nome);
        campo.SetValue(componente, valor);
    }

    private static void Invocar(DetalhesTcc componente, string metodo, params object?[] argumentos)
    {
        var alvo = typeof(DetalhesTcc).GetMethod(metodo, Privados)
            ?? throw new MissingMethodException(nameof(DetalhesTcc), metodo);
        alvo.Invoke(componente, argumentos);
    }

    private static T? LerCampo<T>(DetalhesTcc componente, string nome)
    {
        var campo = typeof(DetalhesTcc).GetField(nome, Privados)
            ?? throw new MissingFieldException(nameof(DetalhesTcc), nome);
        return (T?)campo.GetValue(componente);
    }

    // ── Form inline de feedback por entrega (AbrirFormFeedback / FecharFormFeedback) ──

    [Fact]
    public void AbrirFormFeedback_EstadoInicial_NenhumaEntregaEmAvaliacao()
    {
        var componente = new DetalhesTcc();

        Assert.Null(LerCampo<int?>(componente, "entregaEmAvaliacao"));
    }

    [Fact]
    public void AbrirFormFeedback_EntregaAindaNaoAvaliada_AbreFormVazio()
    {
        // Entrega sem parecer: Feedback null vira string vazia (o DTO exige string não-nula) e
        // Nota permanece nula (campo opcional).
        var componente = new DetalhesTcc();
        var entrega = new Entrega { Id = 10, Titulo = "Capítulo 1", Feedback = null, Nota = null };

        Invocar(componente, "AbrirFormFeedback", entrega);

        Assert.Equal(10, LerCampo<int?>(componente, "entregaEmAvaliacao"));
        var feedback = LerCampo<FeedbackDto>(componente, "feedbackAtual")!;
        Assert.Equal(string.Empty, feedback.Feedback);
        Assert.Null(feedback.Nota);
    }

    [Fact]
    public void AbrirFormFeedback_EntregaJaAvaliada_PreCarregaNotaEParecer()
    {
        // "Editar Avaliação": o form inline abre preenchido com a avaliação existente.
        var componente = new DetalhesTcc();
        var entrega = new Entrega { Id = 3, Feedback = "Revisar a metodologia.", Nota = 8.5m };

        Invocar(componente, "AbrirFormFeedback", entrega);

        var feedback = LerCampo<FeedbackDto>(componente, "feedbackAtual")!;
        Assert.Equal("Revisar a metodologia.", feedback.Feedback);
        Assert.Equal(8.5m, feedback.Nota);
    }

    [Fact]
    public void AbrirFormFeedback_OutraEntrega_TrocaAvaliacaoERecarregaModelo()
    {
        // Abrir o form de outra entrega troca o alvo e substitui o modelo (não mistura os dados
        // digitados na entrega anterior).
        var componente = new DetalhesTcc();
        Invocar(componente, "AbrirFormFeedback", new Entrega { Id = 1, Feedback = "Parecer da 1", Nota = 5m });

        Invocar(componente, "AbrirFormFeedback", new Entrega { Id = 2, Feedback = "Parecer da 2", Nota = 9m });

        Assert.Equal(2, LerCampo<int?>(componente, "entregaEmAvaliacao"));
        var feedback = LerCampo<FeedbackDto>(componente, "feedbackAtual")!;
        Assert.Equal("Parecer da 2", feedback.Feedback);
        Assert.Equal(9m, feedback.Nota);
    }

    [Fact]
    public void AbrirFormFeedback_NaoMutaAEntregaOriginal()
    {
        // O form edita um DTO próprio: alterar o rascunho não altera a entrega carregada do backend
        // enquanto o POST não é feito.
        var componente = new DetalhesTcc();
        var entrega = new Entrega { Id = 4, Feedback = "Original", Nota = 7m };

        Invocar(componente, "AbrirFormFeedback", entrega);
        var feedback = LerCampo<FeedbackDto>(componente, "feedbackAtual")!;
        feedback.Feedback = "Rascunho alterado";
        feedback.Nota = 1m;

        Assert.Equal("Original", entrega.Feedback);
        Assert.Equal(7m, entrega.Nota);
    }

    [Fact]
    public void FecharFormFeedback_LimpaAlvoEModelo()
    {
        // "Cancelar" no form inline: some o form (entregaEmAvaliacao null) e o rascunho é descartado.
        var componente = new DetalhesTcc();
        Invocar(componente, "AbrirFormFeedback", new Entrega { Id = 9, Feedback = "Texto", Nota = 6m });

        Invocar(componente, "FecharFormFeedback");

        Assert.Null(LerCampo<int?>(componente, "entregaEmAvaliacao"));
        var feedback = LerCampo<FeedbackDto>(componente, "feedbackAtual")!;
        Assert.Equal(string.Empty, feedback.Feedback);
        Assert.Null(feedback.Nota);
    }

    // ── Form inline de acompanhamento: criar vs. editar ────────────────────────────

    [Fact]
    public void PrepararEdicaoAcompanhamento_EntraEmModoEdicaoECopiaCampos()
    {
        var componente = new DetalhesTcc();
        DefinirInjetado(componente, "JSRuntime", new FakeJsRuntime());
        var ata = new Acompanhamento
        {
            Id = 15,
            DataReuniao = new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc),
            Ata = "Discussão do capítulo 2."
        };

        Invocar(componente, "PrepararEdicaoAcompanhamento", ata);

        Assert.Equal(15, LerCampo<int?>(componente, "acompanhamentoEmEdicaoId"));
        var dto = LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!;
        Assert.Equal("Discussão do capítulo 2.", dto.Ata);
    }

    [Fact]
    public void PrepararEdicaoAcompanhamento_NaoConverteFusoDaDataReuniao()
    {
        // Paridade de fuso (P6 / arquitetura 6.4): o Client não converte a data — copia o DateTime
        // como veio. A conversão Brasília↔UTC é exclusiva do backend.
        var componente = new DetalhesTcc();
        DefinirInjetado(componente, "JSRuntime", new FakeJsRuntime());
        var original = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        var ata = new Acompanhamento { Id = 1, DataReuniao = original, Ata = "Ata" };

        Invocar(componente, "PrepararEdicaoAcompanhamento", ata);

        var dto = LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!;
        Assert.Equal(original, dto.DataReuniao);
        Assert.Equal(original.Ticks, dto.DataReuniao.Ticks);
        Assert.Equal(original.Kind, dto.DataReuniao.Kind);
    }

    [Fact]
    public void PrepararEdicaoAcompanhamento_DisparaScrollParaOTopo()
    {
        // Comportamento cosmético preservado da versão pré-Radzen (window.scrollTo(0,0) ao abrir a edição).
        var componente = new DetalhesTcc();
        var js = new FakeJsRuntime();
        DefinirInjetado(componente, "JSRuntime", js);

        Invocar(componente, "PrepararEdicaoAcompanhamento", new Acompanhamento { Id = 2, Ata = "Ata" });

        var invocacao = Assert.Single(js.Invocacoes);
        Assert.Equal("window.scrollTo", invocacao.Identifier);
        Assert.Equal(new object?[] { 0, 0 }, invocacao.Args);
    }

    [Fact]
    public void PrepararEdicaoAcompanhamento_NaoMutaAAtaOriginal()
    {
        // Edita um DTO próprio; a ata do histórico só muda depois do PUT + recarga.
        var componente = new DetalhesTcc();
        DefinirInjetado(componente, "JSRuntime", new FakeJsRuntime());
        var ata = new Acompanhamento { Id = 3, DataReuniao = new DateTime(2026, 1, 5), Ata = "Texto original" };

        Invocar(componente, "PrepararEdicaoAcompanhamento", ata);
        LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!.Ata = "Texto editado";

        Assert.Equal("Texto original", ata.Ata);
    }

    [Fact]
    public void PrepararEdicaoAcompanhamento_OutraAta_TrocaOAlvoDaEdicao()
    {
        var componente = new DetalhesTcc();
        DefinirInjetado(componente, "JSRuntime", new FakeJsRuntime());

        Invocar(componente, "PrepararEdicaoAcompanhamento", new Acompanhamento { Id = 1, Ata = "Primeira" });
        Invocar(componente, "PrepararEdicaoAcompanhamento", new Acompanhamento { Id = 2, Ata = "Segunda" });

        Assert.Equal(2, LerCampo<int?>(componente, "acompanhamentoEmEdicaoId"));
        Assert.Equal("Segunda", LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!.Ata);
    }

    [Fact]
    public void CancelarEdicaoAcompanhamento_VoltaParaModoCriacaoComFormLimpo()
    {
        // "Cancelar Edição": o card volta a ser "Nova Ata de Reunião" (o markup usa
        // acompanhamentoEmEdicaoId.HasValue para título, ícone, borda e texto do botão).
        var componente = new DetalhesTcc();
        DefinirInjetado(componente, "JSRuntime", new FakeJsRuntime());
        Invocar(componente, "PrepararEdicaoAcompanhamento",
            new Acompanhamento { Id = 8, DataReuniao = new DateTime(2026, 2, 2), Ata = "Conteúdo" });

        Invocar(componente, "CancelarEdicaoAcompanhamento");

        Assert.Null(LerCampo<int?>(componente, "acompanhamentoEmEdicaoId"));
        var dto = LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!;
        Assert.Equal(string.Empty, dto.Ata);
        Assert.Equal(DateTime.Today, dto.DataReuniao); // default do AcompanhamentoDto
    }

    [Fact]
    public void EstadoInicial_FormDeAcompanhamentoEstaEmModoCriacao()
    {
        var componente = new DetalhesTcc();

        Assert.Null(LerCampo<int?>(componente, "acompanhamentoEmEdicaoId"));
        Assert.Equal(string.Empty, LerCampo<AcompanhamentoDto>(componente, "novoAcompanhamento")!.Ata);
    }

    // ── UrlArquivo: link direto de download da entrega do aluno ────────────────────

    private static string InvocarUrlArquivo(string? baseAddress, string caminho)
    {
        var componente = new DetalhesTcc();
        var http = new HttpClient();
        if (baseAddress != null)
        {
            http.BaseAddress = new Uri(baseAddress);
        }
        DefinirInjetado(componente, "Http", http);

        var metodo = typeof(DetalhesTcc).GetMethod("UrlArquivo", Privados)
            ?? throw new MissingMethodException(nameof(DetalhesTcc), "UrlArquivo");
        return (string)metodo.Invoke(componente, [caminho])!;
    }

    [Fact]
    public void UrlArquivo_BaseComBarraFinal_NaoDuplicaABarra()
    {
        // ArquivoCaminho vem do backend sempre iniciado por "/" (LocalStorageService retorna
        // "/uploads/{categoria}/{arquivo}"); a base do HttpClient termina em "/".
        var url = InvocarUrlArquivo("https://localhost:7145/", "/uploads/entregas/abc_tcc.pdf");

        Assert.Equal("https://localhost:7145/uploads/entregas/abc_tcc.pdf", url);
    }

    [Fact]
    public void UrlArquivo_BaseSemBarraFinal_ConcatenaDireto()
    {
        var url = InvocarUrlArquivo("https://api.exemplo.br/tcc", "/uploads/entregas/abc_tcc.pdf");

        Assert.Equal("https://api.exemplo.br/tcc/uploads/entregas/abc_tcc.pdf", url);
    }

    [Fact]
    public void UrlArquivo_SemBaseAddress_RetornaApenasOCaminho()
    {
        // Guarda de null do operador ?. — não gera "https://null...".
        var url = InvocarUrlArquivo(null, "/uploads/entregas/abc_tcc.pdf");

        Assert.Equal("/uploads/entregas/abc_tcc.pdf", url);
    }
}
