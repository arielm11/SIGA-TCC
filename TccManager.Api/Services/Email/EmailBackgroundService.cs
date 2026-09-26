using MailKit.Net.Smtp;
using MailKit.Security;

namespace TccManager.Api.Services.Email;

/// <summary>
/// Hosted service que consome o Channel de e-mails fora do ciclo da requisição HTTP.
/// Ponto único de try/catch + log Serilog para falha de envio (RF4/RNF1): uma falha de
/// SMTP nunca derruba o worker nem propaga para o request que originou a notificação.
/// Sem DbContext aqui — os dados de destinatário já foram resolvidos antes de a mensagem
/// entrar na fila, evitando ObjectDisposedException de um DbContext scoped já descartado.
/// </summary>
public class EmailBackgroundService : BackgroundService
{
    private readonly IEmailQueue _queue;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailBackgroundService> _logger;

    public EmailBackgroundService(IEmailQueue queue, IEmailService emailService, ILogger<EmailBackgroundService> logger)
    {
        _queue = queue;
        _emailService = emailService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var mensagem in _queue.DequeueAllAsync(stoppingToken))
            {
                try
                {
                    await _emailService.EnviarAsync(mensagem, stoppingToken);
                }
                catch (SmtpCommandException exSmtp)
                {
                    // Issue #99 (achado A09-1): SmtpCommandException carrega o endereço do
                    // destinatário rejeitado na propriedade Mailbox (e possivelmente também
                    // embutido em Message/ToString()) — passar a exceção completa ao logger
                    // (serializada via {Exception} no outputTemplate) vazaria esse dado.
                    // Loga só os códigos estruturados do protocolo SMTP, nunca a exceção
                    // inteira nem exSmtp.Mailbox.
                    _logger.LogWarning(
                        "Falha ao enviar e-mail (SMTP rejeitou o comando). Assunto: {Assunto}, Destinatarios: {QtdDestinatarios}, ErrorCode: {ErrorCode}, StatusCode: {StatusCode}",
                        mensagem.Assunto,
                        mensagem.Destinatarios.Count,
                        exSmtp.ErrorCode,
                        exSmtp.StatusCode);
                }
                // AuthenticationException e SslHandshakeException são MailKit.Security (não
                // System.Security.Authentication) — os tipos que o MailKit efetivamente lança
                // para credencial/TLS invalidos na negociação com o servidor SMTP.
                catch (Exception ex) when (ex is AuthenticationException or SslHandshakeException or NotSupportedException)
                {
                    // Issue #99 (achado A10-1): falha PERMANENTE (TLS quebrado, credencial
                    // inválida, protocolo não suportado) tratada como Error — sinal mais forte
                    // que uma falha transitória de rede, e candidata natural a alerta se/quando
                    // um canal existir (achado A09-2, deliberadamente fora de escopo aqui: a
                    // própria issue registra isso como "item futuro se o volume justificar", não
                    // como ação obrigatória — este projeto não tem infraestrutura de alerta
                    // hoje). Sem risco de vazar destinatário aqui (a causa é TLS/config, não o
                    // endereço), diferente do SmtpCommandException acima.
                    _logger.LogError(
                        ex,
                        "Falha PERMANENTE ao enviar e-mail (TLS/autenticação/configuração) — provavelmente afeta TODOS os envios até ser corrigida. Assunto: {Assunto}, Destinatarios: {QtdDestinatarios}",
                        mensagem.Assunto,
                        mensagem.Destinatarios.Count);
                }
                catch (Exception ex)
                {
                    // Transitória (timeout de rede, SocketException, IOException, etc.) —
                    // mesmo nível/comportamento de antes desta issue.
                    _logger.LogWarning(
                        ex,
                        "Falha ao enviar e-mail. Assunto: {Assunto}, Destinatarios: {QtdDestinatarios}",
                        mensagem.Assunto, mensagem.Destinatarios.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal do host (shutdown) — não é falha de envio.
        }
    }
}
