using System.Runtime.CompilerServices;
using TccManager.Api.Services.Email;

namespace TccManager.Tests.Services.Email;

/// <summary>
/// Substituto de teste para IEmailQueue: captura em memória as mensagens enfileiradas
/// sem depender do Channel/BackgroundService reais nem de SMTP. Permite inspecionar
/// destinatários, assunto e corpo do EmailMessage produzido pela orquestração.
/// </summary>
public class FakeEmailQueue : IEmailQueue
{
    public List<EmailMessage> Mensagens { get; } = new();

    /// <summary>Quando true, simula fila cheia (Enqueue retorna false e não captura).</summary>
    public bool SimularFilaCheia { get; set; }

    public bool Enqueue(EmailMessage mensagem)
    {
        if (SimularFilaCheia)
        {
            return false;
        }

        Mensagens.Add(mensagem);
        return true;
    }

    /// <summary>
    /// Não produz itens (o teste inspeciona <see cref="Mensagens"/> diretamente), mas aguarda
    /// o cancelamento sem lançar exceção não tratada — assim o EmailBackgroundService real,
    /// que consome esta fila via await foreach, não falha e não derruba o host de teste.
    /// </summary>
    public async IAsyncEnumerable<EmailMessage> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        yield break;
    }
}
