using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using TccManager.Api.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Email;

/// <summary>
/// Issue #70, item 1 — TLS obrigatório no envio de e-mail.
///
/// <c>MailKitEmailService.EnviarAsync</c> escolhe <c>SecureSocketOptions.SslOnConnect</c>
/// quando <c>Smtp:UseSsl</c> é true e <c>SecureSocketOptions.StartTls</c> (antes:
/// <c>StartTlsWhenAvailable</c>) quando é false. A diferença entre as duas opções só é
/// observável no protocolo: <c>StartTlsWhenAvailable</c> faz downgrade silencioso para
/// texto puro se o servidor não anuncia a extensão STARTTLS; <c>StartTls</c> derruba a
/// conexão nesse caso.
///
/// A escolha é feita inline dentro de <c>EnviarAsync</c>, logo não há como inspecioná-la
/// sem executar o método — e o <c>SmtpClient</c> do MailKit sempre abre um socket real.
/// A saída aqui é um servidor SMTP falso em <c>127.0.0.1</c> (porta efêmera, apenas
/// loopback, encerrado com o teste): ele fala o mínimo do protocolo e permite distinguir
/// as três situações pelo que chega a ser negociado. Não depende de rede externa, de
/// servidor SMTP instalado nem de certificado.
/// </summary>
public class MailKitEmailServiceTlsTests
{
    private static readonly TimeSpan Limite = TimeSpan.FromSeconds(20);

    private static MailKitEmailService CriarServico(int porta, bool useSsl) =>
        new(Options.Create(new EmailSettings
        {
            From = "SIGA-TCC <noreply@siga-tcc.local>",
            Smtp = new SmtpSettings
            {
                Host = "127.0.0.1",
                Port = porta,
                UseSsl = useSsl
            }
        }));

    private static EmailMessage Mensagem() =>
        new(new List<string> { "destino@teste.com" }, "Assunto de teste", "<p>corpo</p>");

    [Fact]
    public async Task EnviarAsync_UseSslFalse_ServidorSemStartTls_AbortaAConexaoEmVezDeEnviarEmTextoPuro()
    {
        // Núcleo do item 1: com StartTlsWhenAvailable o MailKit seguiria para MAIL FROM /
        // DATA em texto puro. Com StartTls, a ausência da extensão é erro fatal.
        using var servidor = new ServidorSmtpFalso(anunciaStartTls: false);
        using var cts = new CancellationTokenSource(Limite);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => CriarServico(servidor.Porta, useSsl: false).EnviarAsync(Mensagem(), cts.Token));

        Assert.Contains("STARTTLS", ex.Message, StringComparison.OrdinalIgnoreCase);

        var comandos = servidor.ComandosRecebidos();
        // A negociação chegou a acontecer (EHLO enviado), mas nada de conteúdo trafegou.
        Assert.Contains(comandos, c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(comandos, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(comandos, c => c.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnviarAsync_UseSslFalse_ServidorComStartTls_EmiteOComandoStartTls()
    {
        // Lado positivo: quando o servidor anuncia a extensão, o cliente realmente emite
        // STARTTLS antes de qualquer dado (o handshake TLS em si falha porque o servidor
        // falso não tem certificado — o que importa é que o comando foi emitido).
        using var servidor = new ServidorSmtpFalso(anunciaStartTls: true);
        using var cts = new CancellationTokenSource(Limite);

        await Assert.ThrowsAnyAsync<Exception>(
            () => CriarServico(servidor.Porta, useSsl: false).EnviarAsync(Mensagem(), cts.Token));

        var comandos = servidor.ComandosRecebidos();
        Assert.Contains(comandos, c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comandos, c => c.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(comandos, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnviarAsync_UseSslTrue_NegociaTlsAntesDeQualquerComandoSmtp()
    {
        // Ramo SslOnConnect preservado: o TLS é iniciado no próprio connect, então o
        // servidor em texto puro nunca chega a receber um EHLO legível.
        using var servidor = new ServidorSmtpFalso(anunciaStartTls: false);
        using var cts = new CancellationTokenSource(Limite);

        await Assert.ThrowsAnyAsync<Exception>(
            () => CriarServico(servidor.Porta, useSsl: true).EnviarAsync(Mensagem(), cts.Token));

        var comandos = servidor.ComandosRecebidos();
        Assert.DoesNotContain(comandos, c => c.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(comandos, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Servidor SMTP mínimo em loopback: responde ao greeting e ao EHLO, anunciando (ou
    /// não) a extensão STARTTLS, e registra as linhas de comando recebidas. Não implementa
    /// TLS — o objetivo é apenas observar até onde o cliente aceita ir sem criptografia.
    /// </summary>
    private sealed class ServidorSmtpFalso : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly bool _anunciaStartTls;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _comandos = [];
        private readonly Task _atendimento;

        public ServidorSmtpFalso(bool anunciaStartTls)
        {
            _anunciaStartTls = anunciaStartTls;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _atendimento = Task.Run(AtenderAsync);
        }

        public int Porta => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> ComandosRecebidos()
        {
            lock (_comandos)
            {
                return _comandos.ToList();
            }
        }

        private async Task AtenderAsync()
        {
            try
            {
                using var cliente = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var stream = cliente.GetStream();

                var codificacao = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var escritor = new StreamWriter(stream, codificacao) { AutoFlush = true, NewLine = "\r\n" };
                using var leitor = new StreamReader(stream, codificacao);

                await escritor.WriteLineAsync("220 localhost ESMTP servidor-falso");

                while (!_cts.IsCancellationRequested)
                {
                    var linha = await leitor.ReadLineAsync(_cts.Token);
                    if (linha is null) break;

                    lock (_comandos)
                    {
                        _comandos.Add(linha);
                    }

                    if (linha.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("250-localhost");
                        if (_anunciaStartTls)
                        {
                            await escritor.WriteLineAsync("250-STARTTLS");
                        }
                        await escritor.WriteLineAsync("250 HELP");
                    }
                    else if (linha.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("250 localhost");
                    }
                    else if (linha.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        // Aceita o comando e não faz handshake: o cliente falha no TLS,
                        // que é exatamente o desfecho esperado pelo teste.
                        await escritor.WriteLineAsync("220 Ready to start TLS");
                    }
                    else if (linha.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("221 Bye");
                        break;
                    }
                    else
                    {
                        await escritor.WriteLineAsync("502 Command not implemented");
                    }
                }
            }
            catch (Exception)
            {
                // Cancelamento, socket fechado pelo cliente ou bytes de handshake TLS
                // ilegíveis como texto: todos esperados neste harness.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // Listener já encerrado.
            }

            try
            {
                _atendimento.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // O laço de atendimento não propaga falha relevante para o teste.
            }

            _cts.Dispose();
        }
    }
}
