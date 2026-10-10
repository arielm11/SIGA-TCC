using TccManager.Shared.Enums;

namespace TccManager.Shared.Formatting;

/// <summary>
/// Formata <see cref="StatusEntrega"/> para o rótulo amigável exibido nas telas (Aluno/Professor),
/// no lugar do literal <c>ToString()</c> do enum. Mesmo precedente de
/// <see cref="StatusTccFormatter"/>/<see cref="NotaFormatter"/>.
///
/// Issue #155 (achado B6): texto duplicado byte-a-byte entre Aluno/MeuTcc.razor e
/// Professor/DetalhesTcc.razor. O estilo visual (cor do badge Radzen) permanece no Client —
/// Radzen.Blazor não é uma dependência deste projeto.
/// </summary>
public static class StatusEntregaFormatter
{
    public static string Formatar(StatusEntrega status) => status switch
    {
        StatusEntrega.Aprovada => "Aprovada",
        StatusEntrega.Rejeitada => "Rejeitada",
        _ => "Aguardando Veredito"
    };
}
