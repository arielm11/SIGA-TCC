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
                catch (Exception ex)
                {
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
