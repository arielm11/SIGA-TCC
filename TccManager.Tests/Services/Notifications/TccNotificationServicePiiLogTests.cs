using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Services.Notifications;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Notifications;

/// <summary>
/// Issue #70, item 2 — PII (nome) fora do log de destinatário descartado.
///
/// <c>ObterCandidatosCoordenadoresAsync</c> compõe o rótulo "Papel" de cada candidato a
/// destinatário. Esse rótulo só é observável através do <c>LogWarning</c> de
/// <c>ColetarEmails</c>, disparado quando o e-mail do candidato está vazio/em branco.
/// Antes da correção o rótulo era "Coordenador {Nome}", vazando o nome do coordenador
/// para os logs estruturados; agora é "Coordenador {Id}".
///
/// O método é privado, então a verificação é pelo efeito observável: captura-se o
/// <see cref="ILogger"/> injetado no serviço (mais barato que subir o host inteiro com
/// o sink Serilog de <c>ConfiguracaoCustomizadaApiFactory</c>, que só faz sentido para
/// logs emitidos durante o startup).
/// </summary>
public class TccNotificationServicePiiLogTests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;
    private const int IdCoordenadorSemEmail = 77;
    private const string NomeCoordenadorSemEmail = "Fulana Coordenadora Da Silva";

    private static AppDbContext CriarContexto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TccNotificationService CriarServico(
        AppDbContext context, FakeEmailQueue queue, LoggerDeCaptura<TccNotificationService> logger)
        => new(
            context,
            new FileEmailTemplateRenderer(NullLogger<FileEmailTemplateRenderer>.Instance),
            queue,
            logger,
            new RascunhoAtaTokenService(context),
            Options.Create(new AppUrlsOptions()));

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo, bool ativo = true)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = ativo };

    [Fact]
    public async Task NotificarAceiteFinalAsync_CoordenadorSemEmail_LogaOIdENaoONome()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(IdCoordenadorSemEmail, NomeCoordenadorSemEmail, "", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarAceiteFinalAsync(1);

        var aviso = Assert.Single(logger.Entradas, e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Warning, aviso.Nivel);
        Assert.Contains($"Coordenador {IdCoordenadorSemEmail}", aviso.Mensagem, StringComparison.Ordinal);
        Assert.DoesNotContain(NomeCoordenadorSemEmail, aviso.Mensagem, StringComparison.Ordinal);
        // Fragmentos do nome também não podem aparecer (guarda contra um formato do tipo
        // "Coordenador 77 (Fulana)" que passaria na asserção do nome completo).
        Assert.DoesNotContain("Fulana", aviso.Mensagem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silva", aviso.Mensagem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotificarAceiteFinalAsync_CoordenadorSemEmail_PropriedadeEstruturadaPapelNaoContemNome()
    {
        // Log estruturado: o valor de {Papel} vai para o sink como propriedade nomeada.
        // Mascarar só a mensagem renderizada não bastaria — a propriedade também é persistida.
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(IdCoordenadorSemEmail, NomeCoordenadorSemEmail, "   ", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarAceiteFinalAsync(1);

        var aviso = Assert.Single(logger.Entradas, e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal));

        var papel = Assert.IsType<string>(aviso.Propriedades["Papel"]);
        Assert.Equal($"Coordenador {IdCoordenadorSemEmail}", papel);
    }

    [Fact]
    public async Task NotificarResultadoBancaAsync_Reprovado_CoordenadorSemEmail_LogaOIdENaoONome()
    {
        // Segundo (e único outro) chamador de ObterCandidatosCoordenadoresAsync.
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(IdOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(IdCoordenadorSemEmail, NomeCoordenadorSemEmail, "", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, OrientadorId = IdOrientador, MotivoRejeicao = "Nota insuficiente", Status = StatusTcc.AguardandoDefesa });
        context.Banca.Add(new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1", NotaFinal = 40m });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarResultadoBancaAsync(1, aprovado: false);

        var aviso = Assert.Single(logger.Entradas, e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal));

        Assert.Contains($"Coordenador {IdCoordenadorSemEmail}", aviso.Mensagem, StringComparison.Ordinal);
        Assert.DoesNotContain(NomeCoordenadorSemEmail, aviso.Mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotificarAceiteFinalAsync_VariosCoordenadoresSemEmail_LogaUmAvisoPorIdSemNenhumNome()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(41, "Coordenador Alfa", "", TipoUsuario.Coordenador));
        context.Usuarios.Add(NovoUsuario(42, "Coordenador Beta", "   ", TipoUsuario.Coordenador));
        context.Usuarios.Add(NovoUsuario(43, "Coordenador Gama", "gama@teste.com", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarAceiteFinalAsync(1);

        var avisos = logger.Entradas
            .Where(e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, avisos.Count);
        Assert.Contains(avisos, a => a.Mensagem.Contains("Coordenador 41", StringComparison.Ordinal));
        Assert.Contains(avisos, a => a.Mensagem.Contains("Coordenador 42", StringComparison.Ordinal));
        Assert.DoesNotContain(avisos, a => a.Mensagem.Contains("Alfa", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(avisos, a => a.Mensagem.Contains("Beta", StringComparison.OrdinalIgnoreCase));
        // O coordenador com e-mail válido não gera aviso nenhum e continua recebendo.
        Assert.DoesNotContain(avisos, a => a.Mensagem.Contains("Coordenador 43", StringComparison.Ordinal));
        Assert.Contains(queue.Mensagens, m => m.Destinatarios.Contains("gama@teste.com"));
    }

    [Fact]
    public async Task NotificarAceiteFinalAsync_AlunoSemEmail_MantemRotuloFixoSemPii()
    {
        // Os demais papéis já eram literais ("Aluno", "Orientador", ...) e continuam assim:
        // a correção do rótulo do coordenador não pode ter introduzido PII em nenhum outro.
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Beltrano Aluno Sobrenome", "", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(43, "Coordenador Gama", "gama@teste.com", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarAceiteFinalAsync(1);

        var aviso = Assert.Single(logger.Entradas, e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal));

        Assert.Equal("Aluno", aviso.Propriedades["Papel"]);
        Assert.DoesNotContain("Beltrano", aviso.Mensagem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotificarAceiteFinalAsync_TodosComEmailValido_NaoLogaAvisoDeDescarte()
    {
        // Controle negativo: sem candidato descartado, nenhum aviso é emitido — o que
        // garante que as asserções dos testes acima não passam "por vazio".
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();
        var logger = new LoggerDeCaptura<TccNotificationService>();

        context.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(IdCoordenadorSemEmail, NomeCoordenadorSemEmail, "coord@teste.com", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = IdAluno, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue, logger).NotificarAceiteFinalAsync(1);

        Assert.DoesNotContain(logger.Entradas, e => e.Mensagem.Contains("sem e-mail válido", StringComparison.Ordinal));
        Assert.Equal(2, queue.Mensagens.Count);
    }

    /// <summary>
    /// ILogger de teste que guarda nível, mensagem renderizada e as propriedades
    /// estruturadas de cada evento. Substitui o NullLogger usado nos demais testes
    /// unitários do serviço quando o próprio log é o comportamento sob teste.
    /// </summary>
    private sealed class LoggerDeCaptura<T> : ILogger<T>
    {
        private readonly List<EntradaDeLog> _entradas = [];

        public IReadOnlyList<EntradaDeLog> Entradas
        {
            get { lock (_entradas) { return _entradas.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var propriedades = state is IReadOnlyList<KeyValuePair<string, object?>> pares
                ? pares.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();

            lock (_entradas)
            {
                _entradas.Add(new EntradaDeLog(logLevel, formatter(state, exception), propriedades, exception));
            }
        }
    }

    private sealed record EntradaDeLog(
        LogLevel Nivel,
        string Mensagem,
        IReadOnlyDictionary<string, object?> Propriedades,
        Exception? Excecao);
}
