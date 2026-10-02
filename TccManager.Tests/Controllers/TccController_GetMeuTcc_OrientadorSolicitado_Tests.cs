using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #112 (RF04/D7) — GetMeuTcc passa a preencher Tcc.NomeOrientadorSolicitado (campo de
/// transporte [NotMapped]) via consulta projetada, sem alterar o formato de resposta (continua
/// devolvendo a entidade Tcc). D6: a navegação OrientadorSolicitado nunca pode ser serializada,
/// mesmo quando populada por relationship fix-up do EF Core (mesmo DbContext usado pelo
/// TccNotificationService dentro do mesmo escopo de teste).
/// </summary>
public class TccController_GetMeuTcc_OrientadorSolicitado_Tests
{
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo, string senhaHash = "x")
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = senhaHash, Tipo = tipo, Ativo = true };

    [Fact]
    public async Task ComPropostaPendenteEProfessorSolicitado_PreencheNomeOrientadorSolicitado()
    {
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessor, "Professor Desejado", "prof@teste.com", TipoUsuario.Professor));
            ctx.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorSolicitadoId = IdProfessor,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/meu-tcc");

        response.EnsureSuccessStatusCode();
        var tcc = await response.Content.ReadFromJsonAsync<TccDetalheDto>();

        Assert.NotNull(tcc);
        Assert.Equal("Professor Desejado", tcc!.NomeOrientadorSolicitado);
    }

    [Fact]
    public async Task SemProfessorSolicitado_NomeOrientadorSolicitadoPermaneceNulo()
    {
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.Add(NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno));
            ctx.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/meu-tcc");

        response.EnsureSuccessStatusCode();
        var tcc = await response.Content.ReadFromJsonAsync<TccDetalheDto>();

        Assert.NotNull(tcc);
        Assert.Null(tcc!.NomeOrientadorSolicitado);
    }

    [Fact]
    public async Task PayloadBruto_NuncaContemSenhaHashNemEmailDoProfessorSolicitado()
    {
        var hashDoProfessor = BCrypt.Net.BCrypt.HashPassword("senha-super-secreta-do-professor");
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessor, "Professor Desejado", "prof@teste.com", TipoUsuario.Professor, hashDoProfessor));
            ctx.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorSolicitadoId = IdProfessor,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/meu-tcc");

        response.EnsureSuccessStatusCode();
        var corpo = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(hashDoProfessor, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("senhaHash", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prof@teste.com", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Professor Desejado", corpo, StringComparison.Ordinal);
    }
}
