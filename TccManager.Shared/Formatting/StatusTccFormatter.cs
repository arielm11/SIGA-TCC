using TccManager.Shared.Enums;

namespace TccManager.Shared.Formatting;

/// <summary>
/// Formata <see cref="StatusTcc"/> para o rótulo amigável exibido nas telas (Aluno/Professor),
/// no lugar do literal <c>ToString()</c> do enum (que grudava "AguardandoDefesa"/"EmAndamento").
/// Mesmo precedente de <see cref="NotaFormatter"/>: vive em TccManager.Shared para que qualquer
/// consumidor futuro (e-mail, relatório) formate o status do mesmo jeito que o Client.
///
/// Issue #82 — ver docs/arquitetura/2026-08-30-status-tcc-emandamento-nao-usado.md, D8 (seção 8):
/// mapeia o enum inteiro (não só Aprovado/EmAndamento), para não misturar vocabulário formatado
/// com o literal cru no mesmo badge.
/// </summary>
public static class StatusTccFormatter
{
    public static string Formatar(StatusTcc status) => status switch
    {
        StatusTcc.Pendente => "Em análise",
        StatusTcc.Aprovado => "Aguardando 1ª entrega",
        StatusTcc.Reprovado => "Reprovado",
        StatusTcc.EmAndamento => "Em andamento",
        StatusTcc.AguardandoDefesa => "Aguardando defesa",
        StatusTcc.Finalizado => "Finalizado",
        _ => status.ToString()
    };
}
