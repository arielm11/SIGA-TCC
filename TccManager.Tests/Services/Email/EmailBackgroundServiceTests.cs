using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using TccManager.Api.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Email;

/// <summary>
/// Issue #99 (achados A09-1/A10-1): <see cref="EmailBackgroundService"/> passou a distinguir
/// falha PERMANENTE (TLS/autenticação/protocolo — LogError, candidata a alerta) de falha
/// TRANSITÓRIA (rede — LogWarning, comportamento anterior), e a nunca logar a exceção completa
/// de um <see cref="SmtpCommandException"/> (que carrega o endereço do destinatário rejeitado
/// na propriedade Mailbox) — só os códigos estruturados do protocolo SMTP.
/// </summary>
public class EmailBackgroundServiceTests
{
    private static EmailMessage MontarMensagem() =>
        new(new[] { "destinatario@teste.com" }, "Assunto de Teste", "<p>corpo</p>");

    private static async Task<List<(LogLevel Level, Exception? Exception, string Mensagem)>> ExecutarEProcessarUmaMensagemAsync(
        Exception excecaoDoEnvio)
    {
        var queue = new FilaDeUmaMensagem(MontarMensagem());
        var emailService = new EmailServiceQueLanca(excecaoDoEnvio);
        var logger = new LoggerCapturado();
        var servico = new EmailBackgroundService(queue, emailService, logger);

        await servico.StartAsync(CancellationToken.None);
        await servico.ExecuteTask!;

        return logger.Registros;
    }

    [Fact]
    public async Task FalhaDeAutenticacao_LogaComoErrorEDeixaExcecaoCompleta()
    {
        var excecao = new AuthenticationException("credenciais SMTP inválidas");

        var registros = await ExecutarEProcessarUmaMensagemAsync(excecao);

        var registro = Assert.Single(registros);
        Assert.Equal(LogLevel.Error, registro.Level);
        Assert.Same(excecao, registro.Exception);
        Assert.Contains("PERMANENTE", registro.Mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FalhaDeHandshakeTls_LogaComoError()
    {
        var excecao = new SslHandshakeException("handshake TLS falhou");

        var registros = await ExecutarEProcessarUmaMensagemAsync(excecao);

        var registro = Assert.Single(registros);
        Assert.Equal(LogLevel.Error, registro.Level);
    }

    [Fact]
    public async Task FalhaTransitoriaDeRede_ContinuaLogandoComoWarning()
    {
        // Comportamento pré-existente: uma falha de rede comum (não TLS/auth) não deve
        // escalar para Error, senão todo timeout esporádico viraria "sinal de alerta".
        var excecao = new System.Net.Sockets.SocketException();

        var registros = await ExecutarEProcessarUmaMensagemAsync(excecao);

        var registro = Assert.Single(registros);
        Assert.Equal(LogLevel.Warning, registro.Level);
        Assert.Same(excecao, registro.Exception);
    }

    [Fact]
    public async Task FalhaDeComandoSmtp_LogaWarningComCodigosEstruturados_SemAExcecaoCompleta()
    {
        // Issue #99 (achado A09-1): SmtpCommandException carrega o endereço do destinatário
        // rejeitado em Mailbox — passar a exceção inteira ao logger vazaria esse dado (o
        // outputTemplate do Serilog embute {Exception}, que inclui Message/ToString()).
        var excecao = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxNameNotAllowed,
            new MimeKit.MailboxAddress("Alvo", "alvo-rejeitado@teste.com"),
            "550 mailbox not allowed");

        var registros = await ExecutarEProcessarUmaMensagemAsync(excecao);

        var registro = Assert.Single(registros);
        Assert.Equal(LogLevel.Warning, registro.Level);
        Assert.Null(registro.Exception); // nunca a exceção completa
        Assert.Contains("RecipientNotAccepted", registro.Mensagem, StringComparison.Ordinal);
        Assert.Contains("MailboxNameNotAllowed", registro.Mensagem, StringComparison.Ordinal);
        Assert.DoesNotContain("alvo-rejeitado@teste.com", registro.Mensagem, StringComparison.Ordinal);
    }

    private sealed class FilaDeUmaMensagem : IEmailQueue
    {
        private readonly EmailMessage _mensagem;
        public FilaDeUmaMensagem(EmailMessage mensagem) => _mensagem = mensagem;

        public bool Enqueue(EmailMessage mensagem) => throw new NotSupportedException();

        public async IAsyncEnumerable<EmailMessage> DequeueAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return _mensagem;
            await Task.CompletedTask;
        }
    }

    private sealed class EmailServiceQueLanca : IEmailService
    {
        private readonly Exception _excecao;
        public EmailServiceQueLanca(Exception excecao) => _excecao = excecao;

        public Task EnviarAsync(EmailMessage mensagem, CancellationToken cancellationToken = default) =>
            throw _excecao;
    }

    private sealed class LoggerCapturado : ILogger<EmailBackgroundService>
    {
        public List<(LogLevel Level, Exception? Exception, string Mensagem)> Registros { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Registros.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
