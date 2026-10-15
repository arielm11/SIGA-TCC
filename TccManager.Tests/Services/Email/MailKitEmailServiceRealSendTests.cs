using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using TccManager.Api.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Email;

/// <summary>
/// Issue #75 ("teste de envio SMTP real") — <see cref="MailKitEmailServiceTlsTests"/> só cobre
/// os casos em que o handshake TLS FALHA (servidor sem STARTTLS, ou com STARTTLS mas sem
/// certificado). Nenhum teste completava um envio de verdade. Como <c>StartTls</c> é
/// obrigatório (achado #70/A04) e não tem modo texto puro, "enviar de verdade" exige um
/// handshake TLS real — implementado aqui com um certificado autoassinado efêmero (gerado em
/// memória, nunca gravado em disco, descartado no fim do teste) e um
/// <c>ServerCertificateValidationCallback</c> injetado só no <see cref="MailKitEmailService"/>
/// de teste (construtor <c>internal</c>, exposto via <c>InternalsVisibleTo</c> — o construtor
/// público/de produção e o registro de DI continuam exatamente como antes, sem bypass de
/// validação de certificado nenhum em produção).
/// </summary>
public class MailKitEmailServiceRealSendTests
{
    private static readonly TimeSpan Limite = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task EnviarAsync_ComServidorQueCompletaOHandshakeTls_EnviaAMensagemComSucesso()
    {
        using var servidor = new ServidorSmtpComTlsFalso();
        using var cts = new CancellationTokenSource(Limite);

        var settings = Options.Create(new EmailSettings
        {
            From = "SIGA-TCC <noreply@siga-tcc.local>",
            Smtp = new SmtpSettings { Host = "127.0.0.1", Port = servidor.Porta, UseSsl = false }
        });

        var diagnosticoCallback = new List<string>();
        var servico = new MailKitEmailService(settings, () => new SmtpClient
        {
            // Só aceita ESTE certificado efêmero específico (comparação por thumbprint), não
            // "qualquer certificado" — para não mascarar um teste que passaria com qualquer
            // coisa. Escopo exclusivamente deste teste: o SmtpClient de produção nunca define
            // este callback.
            ServerCertificateValidationCallback = (_, cert, chain, erros) =>
            {
                var aceito = cert is X509Certificate2 certX509
                    && certX509.Thumbprint == servidor.Certificado.Thumbprint;
                diagnosticoCallback.Add($"invocado, thumbprint bate: {aceito}, erros: {erros}");
                return aceito;
            }
        });

        var mensagem = new EmailMessage(
            new List<string> { "destino@teste.com" }, "Assunto Real de Teste", "<p>Corpo real de teste</p>");

        try
        {
            await servico.EnviarAsync(mensagem, cts.Token);
        }
        catch (Exception ex)
        {
            await Task.Delay(300);
            Assert.Fail($"Falha no cliente: {ex}\n\nExceção no servidor (se houver): {servidor.ExcecaoDoServidor}\n\nCallback: {string.Join(" | ", diagnosticoCallback)}");
        }

        var comandos = servidor.ComandosRecebidos();
        Assert.Contains(comandos, c => c.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comandos, c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) && c.Contains("noreply@siga-tcc.local", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comandos, c => c.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase) && c.Contains("destino@teste.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comandos, c => c.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(comandos, c => c.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(servidor.CorpoRecebido);
        Assert.Contains("Assunto Real de Teste", servidor.CorpoRecebido);
        Assert.Contains("Corpo real de teste", servidor.CorpoRecebido);
    }

    [Fact]
    public async Task EnviarAsync_CertificadoDoServidorNaoConfiavel_LancaExcecaoDeValidacao()
    {
        // Contraprova: sem o callback aceitando o thumbprint específico (comportamento
        // padrão, igual produção), o handshake TLS falha por certificado não confiável — a
        // mensagem NÃO é enviada. Prova que o teste acima só passa por causa do callback
        // explícito, não porque a validação de certificado esteja de alguma forma desativada.
        using var servidor = new ServidorSmtpComTlsFalso();
        using var cts = new CancellationTokenSource(Limite);

        var settings = Options.Create(new EmailSettings
        {
            From = "SIGA-TCC <noreply@siga-tcc.local>",
            Smtp = new SmtpSettings { Host = "127.0.0.1", Port = servidor.Porta, UseSsl = false }
        });

        var servico = new MailKitEmailService(settings, clientFactory: null); // SmtpClient "de produção", sem callback

        var mensagem = new EmailMessage(new List<string> { "destino@teste.com" }, "Assunto", "<p>corpo</p>");

        await Assert.ThrowsAsync<SslHandshakeException>(() => servico.EnviarAsync(mensagem, cts.Token));

        Assert.DoesNotContain(servidor.ComandosRecebidos(), c => c.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Servidor SMTP mínimo em loopback que completa um handshake TLS server-side de verdade
    /// (certificado autoassinado efêmero, gerado em memória) e depois fala o suficiente do
    /// protocolo (EHLO/MAIL FROM/RCPT TO/DATA/QUIT) para aceitar um envio completo,
    /// registrando os comandos e o corpo da mensagem recebida.
    /// </summary>
    private sealed class ServidorSmtpComTlsFalso : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _comandos = [];
        private readonly Task _atendimento;

        public ServidorSmtpComTlsFalso()
        {
            Certificado = GerarCertificadoEfemero();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _atendimento = Task.Run(AtenderAsync);
        }

        public int Porta => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public X509Certificate2 Certificado { get; }
        public string? CorpoRecebido { get; private set; }

        public IReadOnlyList<string> ComandosRecebidos()
        {
            lock (_comandos)
            {
                return _comandos.ToList();
            }
        }

        private void RegistrarComando(string linha)
        {
            lock (_comandos)
            {
                _comandos.Add(linha);
            }
        }

        /// <summary>
        /// Lê uma linha terminada em "\n" diretamente do <see cref="NetworkStream"/>, um byte
        /// por vez — de propósito, sem nenhum buffer além do necessário para não consumir
        /// bytes que pertencem ao handshake TLS que vem logo em seguida na mesma conexão (ver
        /// comentário em AtenderAsync). Volume de tráfego é mínimo (poucos comandos SMTP
        /// curtos), então o custo do byte-a-byte é irrelevante aqui.
        /// </summary>
        private static async Task<string?> LerLinhaBrutaAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];

            while (true)
            {
                var lidos = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (lidos == 0)
                    return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());

                if (buffer[0] == (byte)'\n')
                {
                    // Remove um "\r" final, se houver (terminador CRLF do protocolo SMTP).
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                        bytes.RemoveAt(bytes.Count - 1);

                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(buffer[0]);
            }
        }

        private async Task AtenderAsync()
        {
            try
            {
                using var cliente = await _listener.AcceptTcpClientAsync(_cts.Token);
                var streamBruto = cliente.GetStream();
                var codificacao = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                // Fase texto puro: greeting -> EHLO -> STARTTLS, depois entrega o socket para
                // o handshake TLS. Escrita via StreamWriter é segura (AutoFlush), mas a
                // LEITURA não pode passar por StreamReader aqui: seu buffer interno lê à
                // frente da linha atual e pode engolir os primeiros bytes do ClientHello TLS
                // (enviado pelo cliente logo após "220 Ready to start TLS"), que nunca
                // chegariam ao SslStream depois — causa um EOF de handshake difícil de
                // diagnosticar. Por isso a fase pré-TLS lê byte a byte direto do socket
                // (ver LerLinhaBrutaAsync), sem nenhum buffer além do necessário.
                var escritorTexto = new StreamWriter(streamBruto, codificacao, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

                await escritorTexto.WriteLineAsync("220 localhost ESMTP servidor-falso-tls");

                while (true)
                {
                    var linha = await LerLinhaBrutaAsync(streamBruto, _cts.Token);
                    if (linha is null) return;
                    RegistrarComando(linha);

                    if (linha.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritorTexto.WriteLineAsync("250-localhost");
                        await escritorTexto.WriteLineAsync("250 STARTTLS");
                    }
                    else if (linha.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritorTexto.WriteLineAsync("220 Ready to start TLS");
                        break;
                    }
                    else
                    {
                        await escritorTexto.WriteLineAsync("502 Command not implemented");
                    }
                }

                await using var sslStream = new SslStream(streamBruto, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(
                    Certificado, clientCertificateRequired: false, checkCertificateRevocation: false);

                var escritor = new StreamWriter(sslStream, codificacao) { AutoFlush = true, NewLine = "\r\n" };
                var leitor = new StreamReader(sslStream, codificacao);

                var corpoLinhas = new List<string>();
                var lendoCorpo = false;

                while (true)
                {
                    var linha = await leitor.ReadLineAsync(_cts.Token);
                    if (linha is null) break;

                    if (lendoCorpo)
                    {
                        if (linha == ".")
                        {
                            lendoCorpo = false;
                            CorpoRecebido = string.Join("\r\n", corpoLinhas);
                            await escritor.WriteLineAsync("250 OK: message queued");
                            continue;
                        }
                        corpoLinhas.Add(linha);
                        continue;
                    }

                    RegistrarComando(linha);

                    if (linha.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("250-localhost");
                        await escritor.WriteLineAsync("250 8BITMIME");
                    }
                    else if (linha.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("250 OK");
                    }
                    else if (linha.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("250 OK");
                    }
                    else if (linha.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await escritor.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>");
                        lendoCorpo = true;
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
            catch (Exception ex)
            {
                // Cancelamento/socket fechado pelo cliente após o QUIT, ou pelo Dispose —
                // esperado neste harness. Guardado para diagnóstico via ExcecaoDoServidor.
                ExcecaoDoServidor = ex;
            }
        }

        public Exception? ExcecaoDoServidor { get; private set; }

        private static X509Certificate2 GerarCertificadoEfemero()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // Server Authentication

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());

            var certificado = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));

            // Reexporta como PFX e recarrega: no Windows, o certificado devolvido direto por
            // CreateSelfSigned às vezes não permite uso da chave privada pelo SslStream
            // (X509KeyStorageFlags.Exportable ausente); o round-trip evita isso de forma
            // confiável, sem gravar nada em disco (Export mantém tudo em memória). Sem
            // EphemeralKeySet de propósito: confirmado empiricamente que o SChannel do
            // Windows rejeita AuthenticateAsServerAsync com chave efêmera ("Authentication
            // failed because the platform does not support ephemeral keys") — a chave
            // associada por este load ainda não grava nada no disco além de um contêiner de
            // chave temporário do próprio SO, descartado com o certificado (Dispose, no
            // Dispose() da classe externa).
            return X509CertificateLoader.LoadPkcs12(
                certificado.Export(X509ContentType.Pfx), password: null,
                X509KeyStorageFlags.Exportable);
        }

        public void Dispose()
        {
            _cts.Cancel();

            try { _listener.Stop(); }
            catch (Exception) { /* já encerrado */ }

            try { _atendimento.Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* não relevante para o teste */ }

            _cts.Dispose();
            Certificado.Dispose();
        }
    }
}
