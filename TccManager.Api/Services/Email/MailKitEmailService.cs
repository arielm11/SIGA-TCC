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
///
/// <c>Smtp:UseSsl = false</c> usa <see cref="SecureSocketOptions.StartTls"/> (obrigatório,
/// não <c>StartTlsWhenAvailable</c> — achado de segurança #70/A04: a versão anterior fazia
/// downgrade silencioso para texto puro se o servidor não anunciasse STARTTLS, expondo
/// credenciais SMTP e o corpo do e-mail a um MITM/STARTTLS-stripping). Isso exige que o
/// catcher local de desenvolvimento (smtp4dev/Papercut) anuncie STARTTLS com um certificado
/// confiado pela máquina — não há bypass de validação de certificado neste código, de
/// propósito. Se o catcher em uso não suportar isso, prefira apontar <c>Smtp:UseSsl = true</c>
/// para uma porta com TLS implícito (<see cref="SecureSocketOptions.SslOnConnect"/>) em vez
/// de reintroduzir um modo "downgrade permitido" aqui.
/// </summary>
public class MailKitEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly Func<SmtpClient> _clientFactory;

    public MailKitEmailService(IOptions<EmailSettings> settings) : this(settings, null)
    {
    }

    // Issue #75 ("teste de envio SMTP real"): construtor de teste — o DI de produção sempre
    // resolve o construtor de um parâmetro acima (clientFactory fica null, cai no
    // "() => new SmtpClient()" abaixo, comportamento inalterado). O parâmetro extra existe só
    // para o teste conseguir injetar um SmtpClient com ServerCertificateValidationCallback
    // apontado para o certificado efêmero autoassinado do servidor SMTP falso — sem isso não
    // haveria como completar um handshake TLS de verdade em teste (StartTls é obrigatório,
    // não tem modo texto puro, por desenho do achado #70/A04) sem manipular o repositório de
    // certificados confiáveis do sistema operacional.
    internal MailKitEmailService(IOptions<EmailSettings> settings, Func<SmtpClient>? clientFactory)
    {
        _settings = settings.Value;
        _clientFactory = clientFactory ?? (() => new SmtpClient());
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

        using var client = _clientFactory();

        // StartTls (não StartTlsWhenAvailable): exige que o servidor suporte STARTTLS e
        // falha a conexão se não suportar, em vez de fazer downgrade silencioso para texto
        // puro quando o servidor não anuncia a extensão.
        var opcoesSocket = _settings.Smtp.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        await client.ConnectAsync(_settings.Smtp.Host, _settings.Smtp.Port, opcoesSocket, cancellationToken);

        if (!string.IsNullOrWhiteSpace(_settings.Smtp.User))
        {
            await client.AuthenticateAsync(_settings.Smtp.User, _settings.Smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
