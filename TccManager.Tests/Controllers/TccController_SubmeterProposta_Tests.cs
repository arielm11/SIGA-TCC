using System.Net.Http.Json;
using System.Text;
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

    // Issue #76 (D4): PropostaTccDto.OrientadorId foi removido do contrato, então esta guarda
    // não pode mais ser escrita com o DTO tipado. Ela NÃO foi apagada: o payload passa a ser
    // JSON cru com a propriedade extra "orientadorId" — versão inclusive mais forte que a
    // tipada, porque exercita o comportamento real do desserializador no pipeline HTTP.
    // Program.cs não configura JsonSerializerOptions.UnmappedMemberHandling = Disallow (só
    // ReferenceHandler.IgnoreCycles), então o padrão do System.Text.Json vale: membro
    // desconhecido é descartado, sem 400 e sem efeito colateral.
    [Fact]
    public async Task Bug4_SubmeterProposta_ComOrientadorIdPreenchido_DeveSerIgnorado()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var jsonCru = """
        {
            "titulo": "Outra Proposta de TCC",
            "resumo": "Resumo qualquer.",
            "orientadorId": 999
        }
        """;
        var conteudo = new StringContent(jsonCru, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/api/tcc/proposta", conteudo);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstOrDefaultAsync(t => t.AlunoId == ID_ALUNO);

        Assert.NotNull(tcc);
        Assert.Equal("Outra Proposta de TCC", tcc!.Titulo);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        // O "orientadorId" extra do corpo não pode ter sido aproveitado por nada.
        Assert.Null(tcc.OrientadorId);
    }
}