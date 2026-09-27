using System.Net;
using Radzen;

namespace TccManager.Client.Handlers;

/// <summary>
/// Issue #107 (achado F-10, regressão de UX introduzida por #74): antes do rate limiting
/// existir nos endpoints de listagem, o usuário nunca esbarrava num limite; agora que existe,
/// sem este handler ele via um erro genérico ao ser bloqueado, em vez de uma mensagem amigável
/// usando o Retry-After que a API já emite corretamente. Tratamento central (não por tela): os
/// 6 endpoints afetados (GestaoProfessores, BancasConcluidas, Professor/Dashboard,
/// Coordenador/Dashboard, AgendamentoBanca + o próprio de professores) não precisam de lógica
/// duplicada.
/// </summary>
public class RateLimitHandler : DelegatingHandler
{
    private readonly NotificationService _notificationService;

    public RateLimitHandler(NotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var detalhe = retryAfter is { } delta
                ? $"Muitas requisições. Tente novamente em {(int)delta.TotalSeconds} segundos."
                : "Muitas requisições. Tente novamente em instantes.";

            _notificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Warning,
                Summary = "Limite de requisições atingido",
                Detail = detalhe,
                Duration = 6000
            });
        }

        return response;
    }
}
