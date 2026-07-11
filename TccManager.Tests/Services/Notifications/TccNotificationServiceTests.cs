using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TccManager.Api.Data;
using TccManager.Api.Services.Email;
using TccManager.Api.Services.Notifications;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Services.Notifications;

/// <summary>
/// Testes unitários de TccNotificationService isolando a orquestração: AppDbContext
/// InMemory dedicado, FileEmailTemplateRenderer real (templates embedded) e FakeEmailQueue
/// para capturar o EmailMessage sem depender do BackgroundService/SMTP. Verificam
/// destinatários e assunto de cada evento, a inclusão de coordenadores nos eventos
/// corretos e o descarte defensivo de e-mails vazios/nulos.
/// </summary>
public class TccNotificationServiceTests
{
    private static AppDbContext CriarContexto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TccNotificationService CriarServico(AppDbContext context, FakeEmailQueue queue)
        => new(
            context,
            new FileEmailTemplateRenderer(),
            queue,
            NullLogger<TccNotificationService>.Instance);

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo, bool ativo = true)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = ativo };

    /// <summary>
    /// Achata os destinatários da fila garantindo o novo contrato de privacidade: uma
    /// EmailMessage por destinatário, cada uma com exatamente 1 e-mail no campo To:.
    /// </summary>
    private static List<string> DestinatariosDaFila(FakeEmailQueue queue)
        => queue.Mensagens.Select(m => Assert.Single(m.Destinatarios)).ToList();

    // ── PropostaAprovada ──────────────────────────────────────────────

    [Fact]
    public async Task NotificarPropostaAprovadaAsync_EnfileiraParaOAluno()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarPropostaAprovadaAsync(1);

        var msg = Assert.Single(queue.Mensagens);
        Assert.Equal("Proposta de TCC aprovada", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
    }

    [Fact]
    public async Task NotificarPropostaAprovadaAsync_TccInexistente_NaoEnfileiraNemLanca()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        await CriarServico(context, queue).NotificarPropostaAprovadaAsync(999);

        Assert.Empty(queue.Mensagens);
    }

    // ── PropostaRejeitada ─────────────────────────────────────────────

    [Fact]
    public async Task NotificarPropostaRejeitadaAsync_EnfileiraParaOAluno()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, MotivoRejeicao = "Escopo amplo", Status = StatusTcc.Pendente });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarPropostaRejeitadaAsync(1);

        var msg = Assert.Single(queue.Mensagens);
        Assert.Equal("Proposta de TCC rejeitada", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
    }

    // ── BancaAgendada ─────────────────────────────────────────────────

    [Fact]
    public async Task NotificarBancaAgendadaAsync_IncluiAlunoOrientadorAvaliadoresInternosEExternos()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(21, "Avaliador Interno", "interno@teste.com", TipoUsuario.Professor));
        context.MembrosExternos.Add(new MembroExterno { Id = 5, Nome = "Externo", Email = "externo@empresa.com", Instituicao = "Empresa" });
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.AguardandoDefesa });

        var banca = new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
        banca.Avaliadores.Add(new BancaAvaliador { Id = 1, BancaId = 1, ProfessorId = 21 });
        banca.Avaliadores.Add(new BancaAvaliador { Id = 2, BancaId = 1, MembroExternoId = 5 });
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarBancaAgendadaAsync(1);

        // Uma mensagem por destinatário (nunca todos no mesmo To:).
        Assert.All(queue.Mensagens, m => Assert.Equal("Banca de TCC agendada", m.Assunto));
        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(4, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.Contains("interno@teste.com", destinatarios);
        Assert.Contains("externo@empresa.com", destinatarios);
        // Nomes dos avaliadores devem aparecer na lista renderizada de cada mensagem.
        Assert.All(queue.Mensagens, m => Assert.Contains("Avaliador Interno", m.CorpoHtml));
        Assert.All(queue.Mensagens, m => Assert.Contains("Externo", m.CorpoHtml));
    }

    [Fact]
    public async Task NotificarBancaAgendadaAsync_AvaliadorSemProfessorNemExterno_EIgnoradoSemDerrubarDemais()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.AguardandoDefesa });

        var banca = new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
        // Avaliador sem ProfessorId nem MembroExternoId resolvido (defesa em profundidade).
        banca.Avaliadores.Add(new BancaAvaliador { Id = 1, BancaId = 1 });
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarBancaAgendadaAsync(1);

        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(2, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
    }

    // ── FeedbackRegistrado ────────────────────────────────────────────

    [Fact]
    public async Task NotificarFeedbackRegistradoAsync_EnfileiraParaOAluno()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, Status = StatusTcc.EmAndamento });
        context.Entregas.Add(new Entrega { Id = 7, TccId = 1, Titulo = "Entrega Parcial", ArquivoCaminho = "/x.pdf", Feedback = "Bom trabalho", Nota = 8.5m });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarFeedbackRegistradoAsync(7);

        var msg = Assert.Single(queue.Mensagens);
        Assert.Equal("Feedback registrado na sua entrega", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
    }

    // ── AceiteFinal (Aluno + Coordenadores ativos) ────────────────────

    [Fact]
    public async Task NotificarAceiteFinalAsync_IncluiAlunoETodosCoordenadoresAtivos()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(30, "Coord Um", "coord1@teste.com", TipoUsuario.Coordenador));
        context.Usuarios.Add(NovoUsuario(31, "Coord Dois", "coord2@teste.com", TipoUsuario.Coordenador));
        context.Usuarios.Add(NovoUsuario(32, "Coord Inativo", "coordinativo@teste.com", TipoUsuario.Coordenador, ativo: false));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarAceiteFinalAsync(1);

        Assert.All(queue.Mensagens, m => Assert.Equal("Aceite final concedido", m.Assunto));
        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(3, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("coord1@teste.com", destinatarios);
        Assert.Contains("coord2@teste.com", destinatarios);
        Assert.DoesNotContain("coordinativo@teste.com", destinatarios);
    }

    // ── ResultadoBanca ────────────────────────────────────────────────

    [Fact]
    public async Task NotificarResultadoBancaAsync_Aprovado_IncluiApenasAlunoEOrientador()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(30, "Coord", "coord@teste.com", TipoUsuario.Coordenador));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.AguardandoDefesa });
        context.Banca.Add(new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1", NotaFinal = 85m });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarResultadoBancaAsync(1, aprovado: true);

        Assert.All(queue.Mensagens, m => Assert.Equal("Resultado final da banca: aprovado", m.Assunto));
        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(2, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.DoesNotContain("coord@teste.com", destinatarios);
    }

    [Fact]
    public async Task NotificarResultadoBancaAsync_Reprovado_IncluiAlunoOrientadorETodosCoordenadoresAtivos()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(30, "Coord Um", "coord1@teste.com", TipoUsuario.Coordenador));
        context.Usuarios.Add(NovoUsuario(31, "Coord Inativo", "coordinativo@teste.com", TipoUsuario.Coordenador, ativo: false));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, MotivoRejeicao = "Nota insuficiente", Status = StatusTcc.AguardandoDefesa });
        context.Banca.Add(new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1", NotaFinal = 40m });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarResultadoBancaAsync(1, aprovado: false);

        Assert.All(queue.Mensagens, m => Assert.Equal("Resultado final da banca: reprovado", m.Assunto));
        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(3, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.Contains("coord1@teste.com", destinatarios);
        Assert.DoesNotContain("coordinativo@teste.com", destinatarios);
        Assert.All(queue.Mensagens, m => Assert.Contains("Nota insuficiente", m.CorpoHtml));
    }

    // ── Filtragem defensiva de e-mail vazio/nulo ──────────────────────

    [Fact]
    public async Task NotificarBancaAgendadaAsync_DestinatarioComEmailVazio_EDescartadoSemLancar()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        // Orientador com e-mail vazio (cenário possível via PUT sem validação).
        context.Usuarios.Add(NovoUsuario(10, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(21, "Avaliador", "aval@teste.com", TipoUsuario.Professor));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.AguardandoDefesa });

        var banca = new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
        banca.Avaliadores.Add(new BancaAvaliador { Id = 1, BancaId = 1, ProfessorId = 21 });
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarBancaAgendadaAsync(1);

        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(2, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("aval@teste.com", destinatarios);
        Assert.DoesNotContain("", destinatarios);
    }

    [Fact]
    public async Task NotificarPropostaAprovadaAsync_UnicoDestinatarioComEmailVazio_NaoEnfileira()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        context.Usuarios.Add(NovoUsuario(10, "Aluno", "   ", TipoUsuario.Aluno));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, Status = StatusTcc.EmAndamento });
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarPropostaAprovadaAsync(1);

        Assert.Empty(queue.Mensagens);
    }

    [Fact]
    public async Task NotificarBancaAgendadaAsync_MesmoEmailEmMaisDeUmPapel_EnviaSemDuplicar()
    {
        using var context = CriarContexto();
        var queue = new FakeEmailQueue();

        // Aluno e orientador com o mesmo e-mail: ColetarEmails aplica Distinct.
        context.Usuarios.Add(NovoUsuario(10, "Aluno", "mesmo@teste.com", TipoUsuario.Aluno));
        context.Usuarios.Add(NovoUsuario(20, "Orientador", "mesmo@teste.com", TipoUsuario.Professor));
        context.Usuarios.Add(NovoUsuario(21, "Avaliador", "aval@teste.com", TipoUsuario.Professor));
        context.Tccs.Add(new Tcc { Id = 1, Titulo = "TCC A", Resumo = "r", AlunoId = 10, OrientadorId = 20, Status = StatusTcc.AguardandoDefesa });

        var banca = new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
        banca.Avaliadores.Add(new BancaAvaliador { Id = 1, BancaId = 1, ProfessorId = 21 });
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        await CriarServico(context, queue).NotificarBancaAgendadaAsync(1);

        // Distinct em ColetarEmails: 2 destinatários únicos, logo 2 mensagens,
        // e "mesmo@teste.com" aparece em exatamente uma delas (não duplicado).
        var destinatarios = DestinatariosDaFila(queue);
        Assert.Equal(2, destinatarios.Count);
        Assert.Single(destinatarios, d => d == "mesmo@teste.com");
    }
}
