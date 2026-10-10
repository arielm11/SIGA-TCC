using Radzen;
using TccManager.Shared.Enums;

namespace TccManager.Client.Shared;

/// <summary>
/// Issue #155 (achado B6): cor do badge Radzen para <see cref="StatusEntrega"/>, duplicada
/// byte-a-byte entre Aluno/MeuTcc.razor e Professor/DetalhesTcc.razor — companheiro de
/// <see cref="TccManager.Shared.Formatting.StatusEntregaFormatter"/> (que cobre só o texto,
/// já que Radzen.Blazor não é dependência de TccManager.Shared).
/// </summary>
public static class StatusEntregaBadgeStyle
{
    public static BadgeStyle Mapear(StatusEntrega status) => status switch
    {
        StatusEntrega.Aprovada => BadgeStyle.Success,
        StatusEntrega.Rejeitada => BadgeStyle.Danger,
        _ => BadgeStyle.Light
    };
}
