using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TccManager.Api.Services.Email;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #112 (RF01/RF02/D8) — TccController.SubmeterProposta passa a aceitar
/// OrientadorSolicitadoId (opcional, P1). Cobre: submissão válida (persiste + notifica o
/// professor), professor inexistente/inativo/não-Professor (400, nada gravado, nada
/// enfileirado) e a não-serialização da navegação (D6).
/// </summary>
public class TccController_SubmeterProposta_OrientadorSolicitado_Tests
{
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private sealed class FactoryComFilaFake : TccApiFactory
    {
        private readonly FakeEmailQueue _fila;

        public FactoryComFilaFake(FakeEmailQueue fila) => _fila = fila;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(_fila);
            });
        }
    }

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo, bool ativo = true)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = ativo };

    private static async Task<FactoryComFilaFake> FactoryComAlunoEProfessor(FakeEmailQueue fila, bool professorAtivo = true, TipoUsuario tipoProfessor = TipoUsuario.Professor)
    {
        var factory = new FactoryComFilaFake(fila);
        using var ctx = factory.CriarContextoDireto();
        ctx.Usuarios.AddRange(
            NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdProfessor, "Professor Desejado", "prof@teste.com", tipoProfessor, professorAtivo));
        await ctx.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task ComProfessorSolicitadoValido_PersisteEEnfileiraNotificacaoParaOProfessor()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComAlunoEProfessor(fila);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = "Sistema de Gestão de TCCs",
            Resumo = "Proposta de um sistema web.",
            OrientadorSolicitadoId = IdProfessor
        };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.AlunoId == IdAluno);
        Assert.Equal(IdProfessor, tcc.OrientadorSolicitadoId);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.OrientadorId);

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal(new[] { "prof@teste.com" }, msg.Destinatarios);
        Assert.Equal("Nova proposta de TCC aguardando sua decisão", msg.Assunto);
    }

    [Fact]
    public async Task SemProfessorSolicitado_NaoEnfileiraNenhumaNotificacao()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComAlunoEProfessor(fila);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo qualquer." };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        response.EnsureSuccessStatusCode();
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.AlunoId == IdAluno);
        Assert.Null(tcc.OrientadorSolicitadoId);
    }

    [Fact]
    public async Task ComProfessorSolicitadoInexistente_RetornaBadRequest_NadaGravadoNadaEnfileirado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComAlunoEProfessor(fila);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo.", OrientadorSolicitadoId = 9999 };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        Assert.False(await verifica.Tccs.AnyAsync(t => t.AlunoId == IdAluno));
    }

    [Fact]
    public async Task ComProfessorSolicitadoInativo_RetornaBadRequest_NadaGravadoNadaEnfileirado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComAlunoEProfessor(fila, professorAtivo: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo.", OrientadorSolicitadoId = IdProfessor };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        Assert.False(await verifica.Tccs.AnyAsync(t => t.AlunoId == IdAluno));
    }

    [Fact]
    public async Task ComIdSolicitadoQueNaoEProfessor_RetornaBadRequest_NadaGravadoNadaEnfileirado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComAlunoEProfessor(fila, tipoProfessor: TipoUsuario.Coordenador);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo.", OrientadorSolicitadoId = IdProfessor };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        Assert.False(await verifica.Tccs.AnyAsync(t => t.AlunoId == IdAluno));
    }

    // ── D6: nem a submissão nem a leitura posterior podem vazar a entidade Usuario do professor ──

    [Fact]
    public async Task RespostaDaSubmissao_PayloadBruto_NuncaContemSenhaHashNemEmailDoProfessor()
    {
        var fila = new FakeEmailQueue();
        var hashDoProfessor = BCrypt.Net.BCrypt.HashPassword("senha-super-secreta-do-professor");
        var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = hashDoProfessor, Tipo = TipoUsuario.Professor, Ativo = true });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo.", OrientadorSolicitadoId = IdProfessor };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);
        response.EnsureSuccessStatusCode();

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(hashDoProfessor, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("senhaHash", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prof@teste.com", corpo, StringComparison.OrdinalIgnoreCase);
        // Case-sensitive: "nomeOrientadorSolicitado" (campo de transporte legítimo, RF04) contém
        // "OrientadorSolicitado" com "O" maiúsculo como substring — só o "orientadorSolicitado"
        // com "o" minúsculo seria a chave JSON da navegação ignorada (D6).
        Assert.DoesNotContain("\"orientadorSolicitado\":", corpo, StringComparison.Ordinal);
    }
}
