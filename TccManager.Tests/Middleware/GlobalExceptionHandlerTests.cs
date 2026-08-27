using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TccManager.Api.Middleware;
using Xunit;

namespace TccManager.Tests.Middleware;

/// <summary>
/// Issue #71 — <see cref="GlobalExceptionHandler"/>, em nível de unidade (sem subir o host):
/// verifica que a exceção original chega inteira ao log (nunca mascarada nem substituída) e
/// que o corpo devolvido ao cliente nunca carrega mensagem, tipo ou stack trace da exceção —
/// mesmo contrato em qualquer ambiente (não há ramo condicional para Development).
/// </summary>
public class GlobalExceptionHandlerTests
{
    private static DefaultHttpContext NovoHttpContext(string method = "POST", string path = "/api/tcc/entregas")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> LerCorpoAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task TryHandleAsync_DevolveTrue_500EProblemDetailsGenerico()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = NovoHttpContext();

        var tratou = await handler.TryHandleAsync(context, new InvalidOperationException("qualquer coisa"), CancellationToken.None);

        Assert.True(tratou);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var corpo = await LerCorpoAsync(context);
        Assert.Contains("Ocorreu um erro inesperado.", corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Falha simulada de SaveChangesAsync ao persistir a Entrega.")]
    [InlineData("Cannot insert duplicate key row in object 'dbo.usuarios' with unique index 'IX_usuarios_Email'. The duplicate key value is (vitima@instituicao.edu.br).")]
    public async Task TryHandleAsync_CorpoDaRespostaNuncaContemAMensagemDaExcecao(string mensagemDaExcecao)
    {
        // As duas mensagens de teste são reais/plausíveis (a segunda é o formato exato que o
        // SQL Server emite numa violação de índice único — a mesma classe de vazamento de
        // PII/detalhe interno que motivou os catches específicos de UsuarioController).
        // GlobalExceptionHandler precisa bloquear isso para QUALQUER exceção, não só as que já
        // têm tratamento dedicado.
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = NovoHttpContext();

        await handler.TryHandleAsync(context, new InvalidOperationException(mensagemDaExcecao), CancellationToken.None);

        var corpo = await LerCorpoAsync(context);
        Assert.DoesNotContain(mensagemDaExcecao, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_NaoMascaraAExcecaoOriginal_LogaOObjetoCompletoIncluindoStackTrace()
    {
        var logger = new LoggerDeCaptura<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = NovoHttpContext();

        Exception excecaoOriginal;
        try
        {
            throw new InvalidOperationException("Falha simulada de SaveChangesAsync ao persistir a Entrega.");
        }
        catch (Exception ex)
        {
            excecaoOriginal = ex; // com stack trace real, como chegaria de um catch (Exception) { ...; throw; }
        }

        await handler.TryHandleAsync(context, excecaoOriginal, CancellationToken.None);

        var entrada = Assert.Single(logger.Entradas);
        Assert.Equal(LogLevel.Error, entrada.Nivel);
        // O objeto de exceção logado é o MESMO, não uma cópia/resumo — comparar por referência
        // é a prova mais forte de que nada foi trocado ou engolido no caminho.
        Assert.Same(excecaoOriginal, entrada.Excecao);
        Assert.NotNull(entrada.Excecao!.StackTrace);
    }

    [Fact]
    public async Task TryHandleAsync_MensagemDeLogContemMetodoERotaMasNaoContemDadoDoUsuario()
    {
        var logger = new LoggerDeCaptura<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = NovoHttpContext(method: "PUT", path: "/api/usuario/42");

        await handler.TryHandleAsync(context, new Exception("erro"), CancellationToken.None);

        var entrada = Assert.Single(logger.Entradas);
        Assert.Contains("PUT", entrada.Mensagem, StringComparison.Ordinal);
        Assert.Contains("/api/usuario/42", entrada.Mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_RotaDeRascunhoDeAta_RedigeOTokenDoPathNoLog()
    {
        // Mesma disciplina de redação já usada em Program.cs (UseSerilogRequestLogging) e
        // RateLimitingSetup — RequestPathRedactor centraliza a regra.
        var logger = new LoggerDeCaptura<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = NovoHttpContext(method: "GET", path: "/api/rascunho-ata/abc123tokenSecreto");

        await handler.TryHandleAsync(context, new Exception("erro"), CancellationToken.None);

        var entrada = Assert.Single(logger.Entradas);
        Assert.DoesNotContain("abc123tokenSecreto", entrada.Mensagem, StringComparison.Ordinal);
        Assert.Contains("/api/rascunho-ata/[REDACTED]", entrada.Mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_CorrelationIdVindoDosItems_ReemiteNoHeaderENoCorpo()
    {
        // Achado A09-1: o ExceptionHandlerMiddleware do framework chama Response.Clear()
        // (que inclui Headers.Clear()) antes de invocar este handler, apagando qualquer
        // header setado antes por CorrelationIdMiddleware. Por isso o CorrelationId tem que
        // vir de HttpContext.Items (que sobrevive ao Clear()), não do header.
        const string correlationId = "11111111-1111-1111-1111-111111111111";
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = NovoHttpContext();
        context.Items[CorrelationIdMiddleware.ItemsKey] = correlationId;

        await handler.TryHandleAsync(context, new InvalidOperationException("erro"), CancellationToken.None);

        Assert.Equal(correlationId, context.Response.Headers["X-Correlation-Id"]);
        var corpo = await LerCorpoAsync(context);
        Assert.Contains(correlationId, corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_OperationCanceledExceptionComRequisicaoAbortada_LogaInformationENaoEscreveResposta()
    {
        // Achado A09-5: cliente cancelou a requisição (aba fechada, timeout do lado dele) não
        // é um erro do servidor — não deve virar Error nem tentar escrever numa conexão que já
        // não existe mais.
        var logger = new LoggerDeCaptura<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = NovoHttpContext();
        context.RequestAborted = new CancellationToken(canceled: true);

        var tratou = await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.True(tratou);
        var entrada = Assert.Single(logger.Entradas);
        Assert.Equal(LogLevel.Information, entrada.Nivel);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task TryHandleAsync_FalhaAoEscreverAResposta_NaoRelancaAExcecaoOriginal_LogaWarningSeparado()
    {
        // Achado A10-1: uma falha secundária (ex.: stream já fechado/abortado) NÃO pode
        // relançar a exceção original — o ExceptionHandlerMiddleware relança o erro capturado
        // se o handler lançar, reabrindo o vazamento de detalhe em Development.
        var logger = new LoggerDeCaptura<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = NovoHttpContext();
        context.Response.Body = new StreamQueLancaAoEscrever();

        var excecaoOriginal = new InvalidOperationException("Falha simulada de SaveChangesAsync ao persistir a Entrega.");

        var tratou = await handler.TryHandleAsync(context, excecaoOriginal, CancellationToken.None);

        Assert.True(tratou);
        Assert.Equal(2, logger.Entradas.Count);
        Assert.Equal(LogLevel.Error, logger.Entradas[0].Nivel);
        Assert.Same(excecaoOriginal, logger.Entradas[0].Excecao);
        Assert.Equal(LogLevel.Warning, logger.Entradas[1].Nivel);
        Assert.NotSame(excecaoOriginal, logger.Entradas[1].Excecao);
    }

    private sealed class StreamQueLancaAoEscrever : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Conexão perdida (simulado).");
    }

    private sealed class LoggerDeCaptura<T> : ILogger<T>
    {
        public List<EntradaDeLog> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entradas.Add(new EntradaDeLog(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record EntradaDeLog(LogLevel Nivel, string Mensagem, Exception? Excecao);
}
