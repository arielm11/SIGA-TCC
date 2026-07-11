using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace TccManager.Api.Services.Email;

/// <summary>
/// Implementação de <see cref="IEmailService"/> via MailKit/SMTP (RF2), apontando em
/// desenvolvimento para um servidor sandbox local (ex.: smtp4dev, Papercut). Não faz
/// try/catch de envio: exceções propagam para o chamador (EmailBackgroundService), que
/// é o ponto central de log de falha (RF4/RNF1). Registrado como Singleton: um SmtpClient
/// novo é criado a cada chamada, pois o cliente MailKit não é thread-safe para reuso
/// concorrente entre múltiplos envios simultâneos.
/// </summary>
public class MailKitEmailService : IEmailService
{
    private readonly EmailSettings _settings;

    public MailKitEmailService(IOptions<EmailSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task EnviarAsync(EmailMessage mensagem, CancellationToken cancellationToken = default)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(_settings.From));

        foreach (var destinatario in mensagem.Destinatarios)
        {
            mimeMessage.To.Add(MailboxAddress.Parse(destinatario));
        }

        mimeMessage.Subject = mensagem.Assunto;
        mimeMessage.Body = new TextPart(MimeKit.Text.TextFormat.Html)
        {
            Text = mensagem.CorpoHtml
        };

        using var client = new SmtpClient();

        var opcoesSocket = _settings.Smtp.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(_settings.Smtp.Host, _settings.Smtp.Port, opcoesSocket, cancellationToken);

        if (!string.IsNullOrWhiteSpace(_settings.Smtp.User))
        {
            await client.AuthenticateAsync(_settings.Smtp.User, _settings.Smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
