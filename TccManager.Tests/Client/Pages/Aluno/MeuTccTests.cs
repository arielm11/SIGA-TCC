using System.Reflection;
using TccManager.Client.Pages.Aluno;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Client.Pages.Aluno;

/// <summary>
/// Testes da lógica C# pura de <see cref="MeuTcc"/> introduzida/alterada na N3 Etapa 3.
///
/// Contexto (à época): não havia infraestrutura bUnit no projeto, então a renderização, o
/// roteamento de layout, o EditForm, o RadzenDataGrid, o upload multipart e as chamadas HTTP NÃO
/// são cobertos aqui — ver docs/testes/2026-07-14-migracao-radzen-blazor-etapa3.md.
///
/// Atualização (issue #75/#81): a infraestrutura bUnit passou a existir. O gate de upload da
/// issue #81 (D13 — formulário some/volta conforme o veredito da Final) e os badges de
/// <c>Entrega.Status</c> são cobertos por renderização real em
/// <see cref="MeuTccGateDeUploadTests"/>. Este arquivo permanece válido: nem
/// <c>AlternarFeedback</c> nem <c>FoiReprovadoNaBanca</c> mudaram na issue #81 (confirmado na
/// seção 11.2 do documento de arquitetura — o mecanismo nunca altera <c>Tcc.Status</c>).
///
/// O que É coberto: os dois trechos de lógica pura do <c>@code</c> que não dependem do ciclo de vida
/// do componente nem de serviços injetados — <see cref="MeuTcc"/>.<c>AlternarFeedback</c> (alternância
/// de exibição do parecer do orientador, decisão de detalhe do frontend-developer citada na tarefa)
/// e <c>FoiReprovadoNaBanca</c> (roteamento "Reprovado na Banca" vs "Proposta Rejeitada"). Ambos são
/// membros privados do componente e não foram extraídos para um serviço/classe própria; por isso são
/// acessados via reflection. O <c>new MeuTcc()</c> não aciona renderer nem injeção — os métodos operam
/// apenas sobre um campo privado (AlternarFeedback) ou sobre o argumento recebido (FoiReprovadoNaBanca).
/// </summary>
public class MeuTccTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void InvocarAlternarFeedback(MeuTcc componente, int entregaId)
    {
        var metodo = typeof(MeuTcc).GetMethod("AlternarFeedback", Privados)
            ?? throw new MissingMethodException(nameof(MeuTcc), "AlternarFeedback");
        metodo.Invoke(componente, [entregaId]);
    }

    private static int? LerEntregaSelecionada(MeuTcc componente)
    {
        var campo = typeof(MeuTcc).GetField("entregaSelecionadaParaFeedback", Privados)
            ?? throw new MissingFieldException(nameof(MeuTcc), "entregaSelecionadaParaFeedback");
        return (int?)campo.GetValue(componente);
    }

    private static bool InvocarFoiReprovadoNaBanca(MeuTcc componente, Tcc tcc)
    {
        var metodo = typeof(MeuTcc).GetMethod("FoiReprovadoNaBanca", Privados)
            ?? throw new MissingMethodException(nameof(MeuTcc), "FoiReprovadoNaBanca");
        return (bool)metodo.Invoke(componente, [tcc])!;
    }

    // ── AlternarFeedback: alternância da exibição do parecer ───────────

    [Fact]
    public void AlternarFeedback_SemSelecaoAtual_SelecionaEntrega()
    {
        var componente = new MeuTcc();

        InvocarAlternarFeedback(componente, 42);

        Assert.Equal(42, LerEntregaSelecionada(componente));
    }

    [Fact]
    public void AlternarFeedback_MesmaEntregaJaSelecionada_Desseleciona()
    {
        // "Ler Feedback" → "Fechar Feedback": clicar de novo na mesma entrega volta a null.
        var componente = new MeuTcc();

        InvocarAlternarFeedback(componente, 7);
        InvocarAlternarFeedback(componente, 7);

        Assert.Null(LerEntregaSelecionada(componente));
    }

    [Fact]
    public void AlternarFeedback_OutraEntregaEnquantoUmaSelecionada_TrocaSelecao()
    {
        // Abrir o parecer de outra entrega troca a seleção (não acumula, não fecha).
        var componente = new MeuTcc();

        InvocarAlternarFeedback(componente, 1);
        InvocarAlternarFeedback(componente, 2);

        Assert.Equal(2, LerEntregaSelecionada(componente));
    }

    [Fact]
    public void AlternarFeedback_EstadoInicial_EhNulo()
    {
        var componente = new MeuTcc();

        Assert.Null(LerEntregaSelecionada(componente));
    }

    [Fact]
    public void AlternarFeedback_SequenciaAbreFechaAbre_TerminaSelecionada()
    {
        var componente = new MeuTcc();

        InvocarAlternarFeedback(componente, 5); // abre 5
        InvocarAlternarFeedback(componente, 5); // fecha 5
        InvocarAlternarFeedback(componente, 5); // abre 5 de novo

        Assert.Equal(5, LerEntregaSelecionada(componente));
    }

    [Fact]
    public void AlternarFeedback_IdZero_ETratadoComoSelecaoValida()
    {
        // Id 0 é um valor de entregaId possível na assinatura int; garante que a comparação
        // usa int? (null vs 0) e não confunde "nada selecionado" com "entrega 0 selecionada".
        var componente = new MeuTcc();

        InvocarAlternarFeedback(componente, 0);

        Assert.Equal(0, LerEntregaSelecionada(componente));
    }

    // ── FoiReprovadoNaBanca: distinção de motivo do reprovação ─────────

    [Fact]
    public void FoiReprovadoNaBanca_SemEntregas_RetornaFalso()
    {
        var componente = new MeuTcc();
        var tcc = new Tcc { Entregas = new List<Entrega>() };

        Assert.False(InvocarFoiReprovadoNaBanca(componente, tcc));
    }

    [Fact]
    public void FoiReprovadoNaBanca_EntregasNulo_RetornaFalso()
    {
        // Guarda de null explícita no método (tcc.Entregas != null && ...).
        var componente = new MeuTcc();
        var tcc = new Tcc { Entregas = null! };

        Assert.False(InvocarFoiReprovadoNaBanca(componente, tcc));
    }

    [Fact]
    public void FoiReprovadoNaBanca_ApenasEntregasParciais_RetornaFalso()
    {
        // Reprovado sem entrega Final = proposta rejeitada pelo orientador, não reprovação na banca.
        var componente = new MeuTcc();
        var tcc = new Tcc
        {
            Entregas = new List<Entrega>
            {
                new() { Tipo = TipoEntrega.Parcial },
                new() { Tipo = TipoEntrega.Parcial }
            }
        };

        Assert.False(InvocarFoiReprovadoNaBanca(componente, tcc));
    }

    [Fact]
    public void FoiReprovadoNaBanca_ComEntregaFinal_RetornaVerdadeiro()
    {
        var componente = new MeuTcc();
        var tcc = new Tcc
        {
            Entregas = new List<Entrega> { new() { Tipo = TipoEntrega.Final } }
        };

        Assert.True(InvocarFoiReprovadoNaBanca(componente, tcc));
    }

    [Fact]
    public void FoiReprovadoNaBanca_MisturaParcialEFinal_RetornaVerdadeiro()
    {
        var componente = new MeuTcc();
        var tcc = new Tcc
        {
            Entregas = new List<Entrega>
            {
                new() { Tipo = TipoEntrega.Parcial },
                new() { Tipo = TipoEntrega.Final },
                new() { Tipo = TipoEntrega.Parcial }
            }
        };

        Assert.True(InvocarFoiReprovadoNaBanca(componente, tcc));
    }
}
