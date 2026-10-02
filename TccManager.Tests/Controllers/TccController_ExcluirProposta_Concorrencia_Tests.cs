using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #145 (achado D6): ExcluirProposta era o único dos 5 endpoints que decidem sobre uma
/// proposta Pendente (DesignarOrientador/RejeitarProposta/AprovarPropostaSolicitada/
/// RejeitarPropostaSolicitada/ExcluirProposta) sem tratamento de
/// DbUpdateConcurrencyException — a mesma corrida (Coordenador decide enquanto o Aluno
/// exclui) virava 500 em vez de 409.
/// </summary>
public class TccController_ExcluirProposta_Concorrencia_Tests
{
    private const int IdAluno = 10;

    [Fact]
    public async Task ConflitoDeConcorrencia_Retorna409ENaoExclui()
    {
        var factory = new SaveChangesFalhaConcorrenciaExclusaoPropostaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            context.Tccs.Add(new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var tccId = (await factory.CriarContextoDireto().Tccs.SingleAsync()).Id;
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.DeleteAsync($"/api/tcc/proposta/{tccId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
