using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #145 (achado B11): o pre-check de aplicação (existeTccAtivo) em SubmeterProposta não
/// impedia duas requisições concorrentes de passarem no pre-check e criarem 2 TCCs ativos
/// para o mesmo Aluno. Backstop: índice único filtrado UX_Tccs_AlunoId_Ativo, tratado como
/// 409 em vez de 500.
/// </summary>
public class TccController_SubmeterProposta_Concorrencia_Tests
{
    private const int IdAluno = 10;

    [Fact]
    public async Task ConflitoDeConcorrencia_Retorna409()
    {
        var factory = new SaveChangesFalhaTccDuplicadoApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo detalhado o suficiente para passar na validação." };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
