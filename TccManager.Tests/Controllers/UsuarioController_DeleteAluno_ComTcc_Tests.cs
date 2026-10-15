using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #147 (achado B6): Tcc→Aluno é Cascade no model — excluir um Aluno com qualquer Tcc
/// (inclusive já Finalizado) apagava em cascata Tcc/Entregas/Banca/BancaAvaliadores/tokens,
/// perdendo o histórico acadêmico, e os arquivos em wwwroot/uploads ficavam órfãos em disco.
/// </summary>
public class UsuarioController_DeleteAluno_ComTcc_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;

    private static async Task<TccApiFactory> CriarFactoryComAlunoAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAdmin, Nome = "Admin", Email = "admin@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Admin, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });

        await context.SaveChangesAsync();
        return factory;
    }

    [Theory]
    [InlineData(StatusTcc.Pendente)]
    [InlineData(StatusTcc.Aprovado)]
    [InlineData(StatusTcc.EmAndamento)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    [InlineData(StatusTcc.Reprovado)]
    public async Task AlunoComTcc_QualquerStatus_Retorna409ENaoExclui(StatusTcc status)
    {
        using var factory = await CriarFactoryComAlunoAsync();
        using (var context = factory.CriarContextoDireto())
        {
            context.Tccs.Add(new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, Status = status, DataCriacao = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdAluno}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("histórico acadêmico", corpo, StringComparison.OrdinalIgnoreCase);

        using var verificacao = factory.CriarContextoDireto();
        Assert.True(await verificacao.Usuarios.AnyAsync(u => u.Id == IdAluno));
        Assert.True(await verificacao.Tccs.AnyAsync(t => t.AlunoId == IdAluno));
    }

    [Fact]
    public async Task AlunoSemTcc_ExcluiNormalmente()
    {
        using var factory = await CriarFactoryComAlunoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.DeleteAsync($"/api/usuario/{IdAluno}");

        response.EnsureSuccessStatusCode();
        using var verificacao = factory.CriarContextoDireto();
        Assert.False(await verificacao.Usuarios.AnyAsync(u => u.Id == IdAluno));
    }

    [Fact]
    public async Task ProfessorComTcc_NaoAfetadoPelaNovaGuarda_ContinuaUsandoPossuiVinculos()
    {
        // Guarda de regressão: a nova checagem é específica para Tipo == Aluno — um Professor
        // com Tcc já é bloqueado pela checagem possuiVinculos existente (OrientadorId), não
        // pela nova. Confirma que a adição não duplicou/alterou esse caminho.
        using var factory = await CriarFactoryComAlunoAsync();
        const int idProfessor = 20;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = idProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
            context.Tccs.Add(new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = idProfessor, Status = StatusTcc.EmAndamento, DataCriacao = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{idProfessor}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("orienta TCC", corpo, StringComparison.OrdinalIgnoreCase);
    }
}
