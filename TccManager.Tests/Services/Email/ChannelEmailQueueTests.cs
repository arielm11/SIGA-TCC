using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TccManager.Api.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Email;

/// <summary>
/// Issue #75 — <see cref="ChannelEmailQueue"/> (a implementação real, bounded, de
/// <see cref="IEmailQueue"/>) nunca tinha teste próprio: os outros testes deste projeto usam
/// <see cref="FakeEmailQueue"/> no lugar dela, então o comportamento de capacidade
/// (1000) e descarte por fila cheia nunca era exercitado de verdade. Puramente em memória —
/// sem SMTP, sem rede, determinístico.
/// </summary>
public class ChannelEmailQueueTests
{
    private sealed class LoggerDeCaptura<T> : ILogger<T>
    {
        public List<(LogLevel Nivel, string Mensagem)> Entradas { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entradas.Add((logLevel, formatter(state, exception)));
        }
    }

    private static EmailMessage NovaMensagem(string assunto = "Assunto") =>
        new(new[] { "destinatario@teste.com" }, assunto, "<p>corpo</p>");

    [Fact]
    public void Enqueue_AteACapacidade_SempreRetornaTrue()
    {
        var fila = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(fila.Enqueue(NovaMensagem($"Assunto {i}")), $"Mensagem {i} deveria ter sido aceita (dentro da capacidade).");
        }
    }

    [Fact]
    public void Enqueue_AlemDaCapacidade_RetornaFalseEDescartaAMensagemNova()
    {
        var fila = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        for (var i = 0; i < 1000; i++)
        {
            fila.Enqueue(NovaMensagem($"Assunto {i}"));
        }

        var aceitouA1001a = fila.Enqueue(NovaMensagem("Mensagem excedente"));

        Assert.False(aceitouA1001a);
    }

    [Fact]
    public void Enqueue_AlemDaCapacidade_LogaWarningComCapacidadeEAssunto()
    {
        var logger = new LoggerDeCaptura<ChannelEmailQueue>();
        var fila = new ChannelEmailQueue(logger);
        for (var i = 0; i < 1000; i++)
        {
            fila.Enqueue(NovaMensagem());
        }

        fila.Enqueue(NovaMensagem("Mensagem que estoura a fila"));

        var entrada = Assert.Single(logger.Entradas);
        Assert.Equal(LogLevel.Warning, entrada.Nivel);
        Assert.Contains("1000", entrada.Mensagem);
        Assert.Contains("Mensagem que estoura a fila", entrada.Mensagem);
    }

    [Fact]
    public async Task DequeueAllAsync_DevolveAsMensagensNaOrdemDeChegada_ENaoDevolveAsDescartadas()
    {
        var fila = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        fila.Enqueue(NovaMensagem("Primeira"));
        fila.Enqueue(NovaMensagem("Segunda"));

        using var cts = new CancellationTokenSource();
        var lidas = new List<string>();
        await foreach (var mensagem in fila.DequeueAllAsync(cts.Token))
        {
            lidas.Add(mensagem.Assunto);
            if (lidas.Count == 2)
            {
                cts.Cancel(); // sinaliza o fim da leitura — o canal nunca é "completado" explicitamente
                break;
            }
        }

        Assert.Equal(new[] { "Primeira", "Segunda" }, lidas);
    }

    [Fact]
    public async Task AlemDaCapacidade_DescartaAMensagemNova_NaoAsMaisAntigas()
    {
        // Trava a premissa de FullMode = DropWrite de que depende o retorno correto de
        // Enqueue (ver comentário no construtor de ChannelEmailQueue): a mensagem REJEITADA é
        // sempre a que está chegando agora, nunca uma das já enfileiradas. Com DropOldest, por
        // exemplo, Enqueue teria retornado true para "Mensagem excedente" e a primeira
        // mensagem original teria sido a descartada — o oposto do que este teste prova.
        var fila = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        fila.Enqueue(NovaMensagem("Primeira original"));
        for (var i = 1; i < 1000; i++)
        {
            fila.Enqueue(NovaMensagem($"Assunto {i}"));
        }

        Assert.False(fila.Enqueue(NovaMensagem("Mensagem excedente")));

        using var cts = new CancellationTokenSource();
        var primeiraLida = default(string);
        await foreach (var mensagem in fila.DequeueAllAsync(cts.Token))
        {
            primeiraLida = mensagem.Assunto;
            cts.Cancel();
            break;
        }

        Assert.Equal("Primeira original", primeiraLida);
    }

    [Fact]
    public async Task AposDrenarAFilaCheia_VoltaAAceitarNovosEnqueues()
    {
        // FullMode = DropWrite descarta a escrita nova quando cheio — não é uma trava
        // permanente: uma vez que o consumidor drena itens, a fila volta a aceitar.
        var fila = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        for (var i = 0; i < 1000; i++)
        {
            fila.Enqueue(NovaMensagem());
        }
        Assert.False(fila.Enqueue(NovaMensagem("Descartada enquanto a fila estava cheia")));

        using var cts = new CancellationTokenSource();
        var lidas = 0;
        await foreach (var _ in fila.DequeueAllAsync(cts.Token))
        {
            lidas++;
            if (lidas == 1000)
            {
                cts.Cancel();
                break;
            }
        }
        Assert.Equal(1000, lidas);

        Assert.True(fila.Enqueue(NovaMensagem("Aceita depois de drenar a fila")));
    }
}
