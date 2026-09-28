using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #113 (achado A06-1): <c>DesignarOrientador</c>/<c>RejeitarProposta</c> faziam
/// read-then-write sobre <see cref="Tcc"/> sem controle de concorrência — dois Coordenadores
/// agindo "ao mesmo tempo" sobre a mesma proposta podiam ambos passar pela guarda
/// (<c>Status == Pendente</c> avaliada só no read) e ambos salvar. Com
/// <c>Tcc.RowVersion</c> ([Timestamp]), o segundo a salvar recebe
/// <see cref="DbUpdateConcurrencyException"/>, que os dois endpoints agora traduzem para 409.
///
/// A exceção é fabricada via <see cref="SaveChangesFalhaConcorrenciaPropostaApiFactory"/> (o
/// InMemory não a produz sozinho — ver TccConcorrenciaOtimistaTests para a limitação).
/// </summary>
public class CoordenadorController_ConcorrenciaOtimistaPropostas_Tests
{
    private const int IdCoordenador = 30;
    private const int IdProfessor = 20;

    private static async Task<int> SemearPropostaPendenteAsync(SaveChangesFalhaConcorrenciaPropostaApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = 10, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        return tcc.Id;
    }

    [Fact]
    public async Task DesignarOrientador_ConflitoDeConcorrencia_Retorna409ENaoLanca500()
    {
        using var factory = new SaveChangesFalhaConcorrenciaPropostaApiFactory();
        var tccId = await SemearPropostaPendenteAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{tccId}/designar-orientador",
            new DesignarOrientadorDto { OrientadorId = IdProfessor });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RejeitarProposta_ConflitoDeConcorrencia_Retorna409ENaoLanca500()
    {
        using var factory = new SaveChangesFalhaConcorrenciaPropostaApiFactory();
        var tccId = await SemearPropostaPendenteAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{tccId}/rejeitar",
            new RejeicaoDto { Motivo = "Fora do escopo do curso." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DesignarOrientador_SemConflito_ContinuaFuncionandoNormalmente()
    {
        // Contraprova: usa a factory normal (sem o interceptor de falha).
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
            context.Tccs.Add(new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = 10, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var tccId = (await factory.CriarContextoDireto().Tccs.SingleAsync()).Id;

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{tccId}/designar-orientador",
            new DesignarOrientadorDto { OrientadorId = IdProfessor });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
