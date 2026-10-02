using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #145 (achado B12): as FKs BancaAvaliador.MembroExterno (NO ACTION) e
/// RascunhoAtaToken.MembroExterno (Restrict) bloqueavam a exclusão de um MembroExterno
/// vinculado, e a DbUpdateException resultante virava 500 em vez de um 409 claro.
/// </summary>
public class CoordenadorController_RemoverMembroExterno_Tests
{
    private const int IdCoordenador = 30;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private static async Task<TccApiFactory> CriarFactoryAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        await context.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task SemVinculo_RemoveComSucesso()
    {
        using var factory = await CriarFactoryAsync();
        using (var context = factory.CriarContextoDireto())
        {
            context.MembrosExternos.Add(new MembroExterno { Id = 1, Nome = "Membro", Email = "membro@empresa.com", Instituicao = "Empresa" });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.DeleteAsync("/api/coordenador/membros-externos/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verificacao = factory.CriarContextoDireto();
        Assert.False(await verificacao.MembrosExternos.AnyAsync(m => m.Id == 1));
    }

    [Fact]
    public async Task ComVinculoDeBanca_Retorna409ENaoExclui()
    {
        using var factory = await CriarFactoryAsync();
        int membroId;
        using (var context = factory.CriarContextoDireto())
        {
            var membro = new MembroExterno { Nome = "Membro", Email = "membro@empresa.com", Instituicao = "Empresa" };
            context.MembrosExternos.Add(membro);

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();

            var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1" };
            context.Banca.Add(banca);
            await context.SaveChangesAsync();

            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = membro.Id });
            await context.SaveChangesAsync();

            membroId = membro.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.DeleteAsync($"/api/coordenador/membros-externos/{membroId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verificacao = factory.CriarContextoDireto();
        Assert.True(await verificacao.MembrosExternos.AnyAsync(m => m.Id == membroId));
    }

    [Fact]
    public async Task Inexistente_Retorna404()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.DeleteAsync("/api/coordenador/membros-externos/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
