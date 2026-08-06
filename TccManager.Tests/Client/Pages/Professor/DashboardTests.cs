using System.Reflection;
using TccManager.Client.Pages.Professor;
using Xunit;

namespace TccManager.Tests.Client.Pages.Professor;

/// <summary>
/// Testes da lógica C# pura de <see cref="Dashboard"/> (Professor) introduzida na N3 Etapa 4.
///
/// Contexto: não há infraestrutura bUnit no projeto (gap pré-existente; RNF-04 não exige teste de UI
/// automatizado). Renderização, <c>RadzenDataGrid</c>/<c>LoadData</c>, <c>DialogService.Confirm</c>,
/// <c>DialogService.OpenAsync&lt;RejeitarPropostaDialog&gt;</c>, <c>NotificationService</c> e as
/// chamadas HTTP NÃO são cobertos aqui — ver docs/testes/2026-07-14-migracao-radzen-blazor-etapa4.md.
///
/// O que É coberto: <c>AlternarResumo(int)</c>, único membro novo desta página que é lógica pura e não
/// depende do ciclo de vida do componente nem de serviços injetados. Ele substitui o
/// <c>data-bs-toggle="collapse"</c> do accordion Bootstrap anterior (a expansão do Resumo da proposta
/// deixou de ser feita pelo JS do Bootstrap e passou a ser estado C# — resolução de P2, alternativa
/// (b) da seção 5.1 da arquitetura). Mesmo padrão de teste de <c>MeuTccTests.AlternarFeedback</c>:
/// membro privado do componente, acessado via reflection sobre <c>new Dashboard()</c> (que não aciona
/// renderer nem injeção de dependência).
/// </summary>
public class DashboardTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void InvocarAlternarResumo(Dashboard componente, int tccId)
    {
        var metodo = typeof(Dashboard).GetMethod("AlternarResumo", Privados)
            ?? throw new MissingMethodException(nameof(Dashboard), "AlternarResumo");
        metodo.Invoke(componente, [tccId]);
    }

    private static int? LerResumoExpandido(Dashboard componente)
    {
        var campo = typeof(Dashboard).GetField("propostaResumoExpandidoId", Privados)
            ?? throw new MissingFieldException(nameof(Dashboard), "propostaResumoExpandidoId");
        return (int?)campo.GetValue(componente);
    }

    // ── AlternarResumo: toggle "Ver Resumo"/"Ocultar" por linha do grid ────────────

    [Fact]
    public void AlternarResumo_EstadoInicial_EhNulo()
    {
        // Nenhuma proposta nasce com o resumo expandido (todas as linhas mostram "Ver Resumo").
        var componente = new Dashboard();

        Assert.Null(LerResumoExpandido(componente));
    }

    [Fact]
    public void AlternarResumo_SemExpansaoAtual_ExpandeAProposta()
    {
        var componente = new Dashboard();

        InvocarAlternarResumo(componente, 42);

        Assert.Equal(42, LerResumoExpandido(componente));
    }

    [Fact]
    public void AlternarResumo_MesmaPropostaJaExpandida_Recolhe()
    {
        // "Ver Resumo" → "Ocultar": clicar de novo na mesma linha volta a null.
        var componente = new Dashboard();

        InvocarAlternarResumo(componente, 7);
        InvocarAlternarResumo(componente, 7);

        Assert.Null(LerResumoExpandido(componente));
    }

    [Fact]
    public void AlternarResumo_OutraPropostaEnquantoUmaExpandida_TrocaExpansao()
    {
        // Expandir outra linha troca a expansão (não acumula, não recolhe) — só um resumo aberto por vez,
        // equivalente ao data-bs-parent do accordion Bootstrap substituído.
        var componente = new Dashboard();

        InvocarAlternarResumo(componente, 1);
        InvocarAlternarResumo(componente, 2);

        Assert.Equal(2, LerResumoExpandido(componente));
    }

    [Fact]
    public void AlternarResumo_SequenciaAbreFechaAbre_TerminaExpandida()
    {
        var componente = new Dashboard();

        InvocarAlternarResumo(componente, 5); // expande 5
        InvocarAlternarResumo(componente, 5); // recolhe 5
        InvocarAlternarResumo(componente, 5); // expande 5 de novo

        Assert.Equal(5, LerResumoExpandido(componente));
    }

    [Fact]
    public void AlternarResumo_IdZero_ETratadoComoExpansaoValida()
    {
        // Garante que a comparação usa int? (null vs 0) e não confunde "nada expandido" com
        // "proposta de Id 0 expandida".
        var componente = new Dashboard();

        InvocarAlternarResumo(componente, 0);

        Assert.Equal(0, LerResumoExpandido(componente));
    }
}
