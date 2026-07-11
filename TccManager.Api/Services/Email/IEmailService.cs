namespace TccManager.Api.Services.Email;

/// <summary>
/// Abstração de envio de e-mail (RF1). Permite trocar a implementação (hoje MailKit/SMTP)
/// sem alterar consumidores. Não é responsabilidade desta interface tratar falhas de
/// forma silenciosa — quem chama (EmailBackgroundService) é o ponto central de try/catch.
/// </summary>
public interface IEmailService
{
    Task EnviarAsync(EmailMessage mensagem, CancellationToken cancellationToken = default);
}
