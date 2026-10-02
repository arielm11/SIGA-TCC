using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #143 (achados M6/D3/D4): cobre a migração de UsuarioController para o canal
/// dedicado de auditoria ("TccManager.Api.Auditoria", em vez do logger genérico), a adição de
/// ILogger/auditoria em AvaliadorController (antes sem nenhum logger), e os eventos de
/// auditoria adicionados em pontos que não tinham nenhum registro (AgendarBanca,
/// RegistrarResultadoBanca, AtualizarCapacidade, AdicionarMembroExterno/RemoverMembroExterno,
/// ReenviarRascunhoAta, ExcluirProposta, RegistrarFeedback, CRUD de acompanhamentos, troca de
/// senha/e-mail em UpdateUsuario, login bem-sucedido).
/// </summary>
public class AuditoriaCoberturaInconsistente_Tests
{
    private const string CategoriaAuditoria = "TccManager.Api.Auditoria";
    private const int IdAdmin = 1;
    private const int IdCoordenador = 30;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private static void AssertEhAuditoria(LogEvent entrada) =>
        Assert.Contains(CategoriaAuditoria, entrada.Properties["SourceContext"].ToString(), StringComparison.Ordinal);

    // ───────────────────────── UsuarioController (achado D3) ─────────────────────────

    [Fact]
    public async Task CreateUsuario_LogDeCriacao_VaiParaOCanalDeAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAdmin, Nome = "Admin", Email = "admin@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Admin, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var dto = new UsuarioDto { Nome = "Novo Aluno", Email = "novo-aluno@teste.com", Senha = "senha-valida-123456", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Usuário criado com sucesso", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Equal(LogEventLevel.Information, entrada.Level);
    }

    [Fact]
    public async Task DeleteUsuario_LogDeExclusao_VaiParaOCanalDeAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAdmin, Nome = "Admin", Email = "admin@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Admin, Ativo = true },
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdAluno}");
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Usuário excluído com sucesso", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
    }

    [Fact]
    public async Task GetUsuarioById_AcessoNegado_VaiParaOCanalDeAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = 11, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var response = await client.GetAsync("/api/usuario/11");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Acesso negado em GET", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Equal(LogEventLevel.Warning, entrada.Level);
    }

    [Fact]
    public async Task UpdateUsuario_TrocaDeSenha_RegistraEventoDeAuditoriaExplicito()
    {
        const string senhaOriginal = "senha-original-123456";
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = BCrypt.Net.BCrypt.HashPassword(senhaOriginal), Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var dto = new UsuarioDto { Nome = "Aluno", Email = "aluno@teste.com", Senha = "nova-senha-123456", SenhaAtual = senhaOriginal, Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Dados sensíveis alterados em PUT", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains("SenhaMudou: True", entrada.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains("EmailMudou: False", entrada.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateUsuario_AlterandoApenasNome_NaoRegistraEventoDeDadosSensiveis()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var dto = new UsuarioDto { Nome = "Nome Novo", Email = "aluno@teste.com", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);
        response.EnsureSuccessStatusCode();

        Assert.DoesNotContain(factory.LogsDoHost, e => e.RenderMessage().Contains("Dados sensíveis alterados em PUT", StringComparison.Ordinal));
    }

    // ───────────────────────── AvaliadorController (achado D4) ─────────────────────────

    [Fact]
    public async Task GetAtaRascunhoPdf_AcessoNegado_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdProfessor, Nome = "Professor Sem Vinculo", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");
        var response = await client.GetAsync("/api/avaliador/banca/9999/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Acesso negado ao rascunho da ata", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains($"BancaId: 9999", entrada.RenderMessage(), StringComparison.Ordinal);
    }

    // ───────────────────────── CoordenadorController ─────────────────────────

    [Fact]
    public async Task AgendarBanca_Sucesso_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int tccId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = 21, Nome = "Avaliador 1", Email = "aval1@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = 22, Nome = "Avaliador 2", Email = "aval2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var dto = new AgendarBancaDto { DataHora = DateTime.Now.AddDays(7), Local = "Sala 1", ProfessoresIds = new List<int> { 21, 22 }, MembrosExternosIds = new List<int>() };

        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Banca agendada.", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains($"TccId: {tccId}", entrada.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtualizarCapacidade_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.PutAsJsonAsync($"/api/coordenador/professores/{IdProfessor}/capacidade", new CapacidadeProfessorDto { LimiteOrientandos = 3, AceitandoOrientandos = false });

        response.EnsureSuccessStatusCode();
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Capacidade de orientação atualizada", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains($"ProfessorId: {IdProfessor}", entrada.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdicionarMembroExterno_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto { Nome = "Membro", Email = "m@empresa.com", Instituicao = "Empresa" });

        response.EnsureSuccessStatusCode();
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Membro externo criado", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
    }

    [Fact]
    public async Task RemoverMembroExterno_Sucesso_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int membroId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            var membro = new MembroExterno { Nome = "Membro", Email = "m@empresa.com", Instituicao = "Empresa" };
            context.MembrosExternos.Add(membro);
            await context.SaveChangesAsync();
            membroId = membro.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.DeleteAsync($"/api/coordenador/membros-externos/{membroId}");

        response.EnsureSuccessStatusCode();
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Membro externo removido com sucesso", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
    }

    [Fact]
    public async Task ReenviarRascunhoAta_RegistraAuditoriaSemVazarOToken()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int bancaId, membroId;
        using (var context = factory.CriarContextoDireto())
        {
            var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
            var coordenador = new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true };
            context.Usuarios.AddRange(aluno, coordenador);
            await context.SaveChangesAsync();

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = aluno.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();

            var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
            context.Banca.Add(banca);
            await context.SaveChangesAsync();

            var membro = new MembroExterno { Nome = "Externo", Email = "ext@empresa.com", Instituicao = "Empresa" };
            context.MembrosExternos.Add(membro);
            await context.SaveChangesAsync();

            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = membro.Id });
            await context.SaveChangesAsync();

            bancaId = banca.Id;
            membroId = membro.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.PostAsync($"/api/coordenador/banca/{bancaId}/membro-externo/{membroId}/reenviar-rascunho", null);

        response.EnsureSuccessStatusCode();
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Novo token de rascunho de ata emitido", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains($"BancaId: {bancaId}", entrada.RenderMessage(), StringComparison.Ordinal);
        Assert.Contains($"MembroExternoId: {membroId}", entrada.RenderMessage(), StringComparison.Ordinal);
    }

    // ───────────────────────── OrientadorController ─────────────────────────

    [Fact]
    public async Task RegistrarFeedback_RegistraAuditoriaComNotaMasNuncaOTextoDoParecer()
    {
        const string textoDistintivo = "PARECER-SECRETO-QUE-NAO-PODE-VAZAR-NO-LOG-7f3a";
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int entregaId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor, Status = StatusTcc.EmAndamento, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();

            var entrega = new Entrega { TccId = tcc.Id, Titulo = "Parcial", ArquivoCaminho = "/uploads/entregas/x.pdf", Tipo = TipoEntrega.Parcial, DataEnvio = DateTime.UtcNow };
            context.Entregas.Add(entrega);
            await context.SaveChangesAsync();
            entregaId = entrega.Id;
        }

        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");
        var response = await client.PostAsJsonAsync($"/api/orientador/entregas/{entregaId}/feedback", new FeedbackDto { Feedback = textoDistintivo, Nota = 7.5m });

        response.EnsureSuccessStatusCode();
        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(logs, e => e.RenderMessage().Contains("Feedback registrado.", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains("Nota: 7.5", entrada.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(textoDistintivo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrudDeAcompanhamento_RegistraAuditoriaSemVazarOTextoDaAta()
    {
        const string textoDistintivo = "ATA-SECRETA-QUE-NAO-PODE-VAZAR-NO-LOG-9c1e";
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int tccId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor, Status = StatusTcc.EmAndamento, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var criar = await client.PostAsJsonAsync($"/api/orientador/tcc/{tccId}/acompanhamentos", new AcompanhamentoDto { DataReuniao = DateTime.Today, Ata = textoDistintivo });
        criar.EnsureSuccessStatusCode();

        using var contextoLeitura = factory.CriarContextoDireto();
        var acompanhamentoId = (await contextoLeitura.Acompanhamentos.SingleAsync()).Id;

        var editar = await client.PutAsJsonAsync($"/api/orientador/tcc/{tccId}/acompanhamentos/{acompanhamentoId}", new AcompanhamentoDto { DataReuniao = DateTime.Today, Ata = "Editado" });
        editar.EnsureSuccessStatusCode();

        var excluir = await client.DeleteAsync($"/api/orientador/tcc/{tccId}/acompanhamentos/{acompanhamentoId}");
        excluir.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        Assert.Single(logs, e => e.RenderMessage().Contains("Acompanhamento registrado.", StringComparison.Ordinal));
        Assert.Single(logs, e => e.RenderMessage().Contains("Acompanhamento editado.", StringComparison.Ordinal));
        Assert.Single(logs, e => e.RenderMessage().Contains("Acompanhamento excluído.", StringComparison.Ordinal));
        Assert.All(
            logs.Where(e => e.RenderMessage().Contains("Acompanhamento", StringComparison.Ordinal)),
            AssertEhAuditoria);
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(textoDistintivo, StringComparison.Ordinal));
    }

    // ───────────────────────── TccController ─────────────────────────

    [Fact]
    public async Task ExcluirProposta_Sucesso_RegistraAuditoria()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        int tccId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var response = await client.DeleteAsync($"/api/tcc/proposta/{tccId}");

        response.EnsureSuccessStatusCode();
        var entrada = Assert.Single(factory.LogsDoHost, e => e.RenderMessage().Contains("Proposta excluída pelo próprio Aluno", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
    }

    // ───────────────────────── AuthController ─────────────────────────

    [Fact]
    public async Task Login_Sucesso_RegistraAuditoriaSemVazarOEmail()
    {
        const string email = "aluno-login-teste@teste.com";
        const string senha = "senha-valida-123456";
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = email, SenhaHash = BCrypt.Net.BCrypt.HashPassword(senha), Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Email = email, Senha = senha });

        response.EnsureSuccessStatusCode();
        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(logs, e => e.RenderMessage().Contains("Login bem-sucedido", StringComparison.Ordinal));
        AssertEhAuditoria(entrada);
        Assert.Contains($"UsuarioId: {IdAluno}", entrada.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(email, StringComparison.Ordinal));
    }
}
