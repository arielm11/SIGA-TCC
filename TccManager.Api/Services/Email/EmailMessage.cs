namespace TccManager.Api.Services.Email;

/// <summary>
/// Mensagem de e-mail já pronta para envio: destinatários resolvidos e corpo já
/// renderizado. É o único objeto que cruza do request (onde é montado, com acesso
/// ao AppDbContext scoped) para o Channel consumido pelo EmailBackgroundService.
/// O remetente não faz parte deste objeto — vem de EmailSettings no momento do envio.
/// </summary>
public sealed class EmailMessage
{
    public IReadOnlyList<string> Destinatarios { get; }
    public string Assunto { get; }
    public string CorpoHtml { get; }

    public EmailMessage(IReadOnlyList<string> destinatarios, string assunto, string corpoHtml)
    {
        Destinatarios = destinatarios;
        Assunto = assunto;
        CorpoHtml = corpoHtml;
    }
}
