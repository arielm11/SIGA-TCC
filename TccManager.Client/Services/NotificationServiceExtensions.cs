using Radzen;

namespace TccManager.Client.Services;

/// <summary>
/// Issue #156 (achado B8): boilerplate de notificação de erro/sucesso/aviso duplicado 84
/// vezes em 16 arquivos, com durações inconsistentes sem critério para ações equivalentes
/// (4000/5000/6000ms para o mesmo tipo de aviso). Padroniza em 5000ms (valor já majoritário)
/// via métodos de extensão sobre NotificationService — duration explícito ainda é aceito
/// para o raro caso que precisar de um valor diferente.
/// </summary>
public static class NotificationServiceExtensions
{
    public const int DuracaoPadraoMs = 5000;

    public static void NotificarErro(this NotificationService service, string summary, string detail, int duration = DuracaoPadraoMs) =>
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = summary,
            Detail = detail,
            Duration = duration
        });

    public static void NotificarSucesso(this NotificationService service, string summary, string detail, int duration = DuracaoPadraoMs) =>
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = summary,
            Detail = detail,
            Duration = duration
        });

    public static void NotificarAviso(this NotificationService service, string summary, string detail, int duration = DuracaoPadraoMs) =>
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Warning,
            Summary = summary,
            Detail = detail,
            Duration = duration
        });
}
