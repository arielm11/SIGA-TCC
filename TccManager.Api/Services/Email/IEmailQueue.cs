namespace TccManager.Api.Services.Email;

/// <summary>
/// Fila em memória (produtor/consumidor) usada para desacoplar a latência do SMTP do
/// ciclo da requisição HTTP (RF3/RNF2). Não é uma fila durável: mensagens pendentes são
/// perdidas se o processo cair — decisão de produto aceita explicitamente (sem retry,
/// sem mensageria persistente neste MVP).
/// </summary>
public interface IEmailQueue
{
    /// <summary>
    /// Enfileira a mensagem para envio posterior. Retorna false se a fila estiver cheia
    /// (mensagem descartada) — quem chama deve logar esse caso.
    /// </summary>
    bool Enqueue(EmailMessage mensagem);

    IAsyncEnumerable<EmailMessage> DequeueAllAsync(CancellationToken cancellationToken);
}
