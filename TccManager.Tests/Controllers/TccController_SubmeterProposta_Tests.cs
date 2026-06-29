using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

public class TccController_SubmeterProposta_Tests
{
    private const int ID_ALUNO = 10;

    private async Task<TccApiFactory> PrepararCenarioComAluno()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Id = ID_ALUNO, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        context.Usuarios.Add(aluno);
        await context.SaveChangesAsync();

        return factory;
    }

    [Fact]
    public async Task Bug4_SubmeterProposta_SemOrientadorId_DeveSerAceitaComSucesso()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = "Sistema de Gestão de TCCs",
            Resumo = "Proposta de um sistema web para gerenciar o ciclo de vida de TCCs."
            // OrientadorId não é definido — fica no valor default (0)
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == ID_ALUNO);

        Assert.NotNull(tcc);
        Assert.Equal("Sistema de Gestão de TCCs", tcc!.Titulo);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);

        Assert.Null(tcc.OrientadorId);
    }

    [Fact]
    public async Task Bug4_SubmeterProposta_ComOrientadorIdPreenchido_DeveSerIgnorado()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = "Outra Proposta de TCC",
            Resumo = "Resumo qualquer.",
            OrientadorId = 999 // valor arbitrário, não deveria ter efeito
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == ID_ALUNO);

        Assert.NotNull(tcc);
        Assert.Null(tcc!.OrientadorId);
    }
}