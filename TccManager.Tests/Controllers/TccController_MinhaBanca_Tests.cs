using System.Net.Http.Json;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #160 (achado G1): GetMinhaBanca projetava só <c>a.Professor?.Nome</c> — uma banca
/// com avaliador externo (MembroExterno) devolvia um nome nulo/vazio na lista exibida ao
/// Aluno. Mesmo raciocínio já aplicado em CoordenadorController.GetBancasPendentesResultado.
/// </summary>
public class TccController_MinhaBanca_Tests
{
    private sealed record MinhaBancaResponse(DateTime DataHora, string Local, List<string?> Professores);

    private static async Task<(TccApiFactory Factory, int AlunoId)> SemearComAvaliadoresAsync(
        bool comMembroExterno)
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var professor = new Usuario { Nome = "Professor Interno", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, professor);
        await context.SaveChangesAsync();

        var tcc = new Tcc { Titulo = "TCC", Resumo = "r", AlunoId = aluno.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = professor.Id });

        if (comMembroExterno)
        {
            var membroExterno = new MembroExterno { Nome = "Avaliadora Externa", Email = "externa@teste.com", Instituicao = "Outra Universidade" };
            context.MembrosExternos.Add(membroExterno);
            await context.SaveChangesAsync();

            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = membroExterno.Id });
        }

        await context.SaveChangesAsync();

        return (factory, aluno.Id);
    }

    [Fact]
    public async Task ComAvaliadorExterno_NomeDoMembroExternoApareceNaLista()
    {
        var (factory, alunoId) = await SemearComAvaliadoresAsync(comMembroExterno: true);
        using var f = factory;
        var client = f.CreateClientAutenticado(alunoId, "Aluno");

        var response = await client.GetAsync("/api/tcc/minha-banca");
        response.EnsureSuccessStatusCode();

        var corpo = await response.Content.ReadFromJsonAsync<MinhaBancaResponse>();

        Assert.NotNull(corpo);
        Assert.Equal(2, corpo!.Professores.Count);
        Assert.Contains("Professor Interno", corpo.Professores);
        Assert.Contains("Avaliadora Externa", corpo.Professores);
        Assert.DoesNotContain(null, corpo.Professores);
    }

    [Fact]
    public async Task SomenteAvaliadoresInternos_ListaContinuaCorreta()
    {
        var (factory, alunoId) = await SemearComAvaliadoresAsync(comMembroExterno: false);
        using var f = factory;
        var client = f.CreateClientAutenticado(alunoId, "Aluno");

        var response = await client.GetAsync("/api/tcc/minha-banca");
        response.EnsureSuccessStatusCode();

        var corpo = await response.Content.ReadFromJsonAsync<MinhaBancaResponse>();

        Assert.NotNull(corpo);
        Assert.Equal(new List<string?> { "Professor Interno" }, corpo!.Professores);
    }
}
