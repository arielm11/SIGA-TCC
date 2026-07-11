using System.Threading.Channels;

namespace TccManager.Api.Services.Email;

/// <summary>
/// Implementação de <see cref="IEmailQueue"/> via System.Threading.Channels. Canal
/// bounded (capacidade ~1000) com FullMode = DropWrite: se encher, o produtor descarta
/// a mensagem mais nova e loga um warning, em vez de bloquear a requisição HTTP —
/// consistente com a política de "falha silenciosa" e improvável no volume local do projeto.
/// </summary>
public class ChannelEmailQueue : IEmailQueue
{
    private const int Capacidade = 1000;

    private readonly Channel<EmailMessage> _channel;
    private readonly ILogger<ChannelEmailQueue> _logger;

    public ChannelEmailQueue(ILogger<ChannelEmailQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(Capacidade)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool Enqueue(EmailMessage mensagem)
    {
        var sucesso = _channel.Writer.TryWrite(mensagem);

        if (!sucesso)
        {
            _logger.LogWarning(
                "Fila de e-mails cheia (capacidade {Capacidade}); mensagem descartada. Assunto: {Assunto}, Destinatarios: {QtdDestinatarios}",
                Capacidade, mensagem.Assunto, mensagem.Destinatarios.Count);
        }

        return sucesso;
    }

    public IAsyncEnumerable<EmailMessage> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
