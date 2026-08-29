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

        // Issue #75 (achado encontrado escrevendo o teste desta classe, confirmado
        // empiricamente): com FullMode = DropWrite, Writer.TryWrite SEMPRE retorna true,
        // mesmo quando o item é descartado por falta de espaço — o retorno não serve para
        // detectar descarte (contrato documentado do próprio DropWrite: o item sendo escrito
        // é dropado, não a escrita em si). O "if (!sucesso)" que existia aqui antes desta
        // issue era código morto: o warning de fila cheia nunca disparava.
        //
        // Corrigido com o overload de CreateBounded que recebe um callback "itemDropped": o
        // runtime invoca esse callback exatamente quando (e só quando) um item é descartado,
        // de forma atômica em relação ao drop — ao contrário de checar Reader.Count antes de
        // escrever (abordagem anterior desta correção, descartada por ter uma corrida
        // estreita entre múltiplos produtores concorrentes: podia descartar uma mensagem que
        // o consumidor já tinha liberado espaço para aceitar). Para DropWrite especificamente,
        // o item descartado é sempre o próprio item que estava sendo escrito (não um item
        // antigo sendo despejado) — por isso comparar por referência contra a mensagem desta
        // chamada, via ThreadStatic, é seguro: TryWrite invoca o callback de forma síncrona,
        // no mesmo thread, antes de retornar (sem await no meio), então threads concorrentes
        // nunca compartilham o mesmo slot.
        _channel = Channel.CreateBounded<EmailMessage>(
            new BoundedChannelOptions(Capacidade)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            },
            itemDropped: descartada =>
            {
                _ultimaDescartadaNesteThread = descartada;
                _logger.LogWarning(
                    "Fila de e-mails cheia (capacidade {Capacidade}); mensagem descartada. Assunto: {Assunto}, Destinatarios: {QtdDestinatarios}",
                    Capacidade, descartada.Assunto, descartada.Destinatarios.Count);
            });
    }

    [ThreadStatic]
    private static EmailMessage? _ultimaDescartadaNesteThread;

    public bool Enqueue(EmailMessage mensagem)
    {
        _ultimaDescartadaNesteThread = null;
        _channel.Writer.TryWrite(mensagem); // sempre retorna true com DropWrite — ver comentário acima
        return !ReferenceEquals(_ultimaDescartadaNesteThread, mensagem);
    }

    public IAsyncEnumerable<EmailMessage> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
