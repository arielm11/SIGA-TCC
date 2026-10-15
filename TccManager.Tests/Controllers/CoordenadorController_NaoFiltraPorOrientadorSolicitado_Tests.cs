using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #112 (decisão de produto 4.2/4.7, RNF05) — GetPropostasPendentes/DesignarOrientador/
/// RejeitarProposta do Coordenador continuam exatamente como estão hoje, sem filtrar por
/// OrientadorSolicitadoId: o Coordenador mantém visibilidade e autoridade sobre toda proposta
/// Pendente, com ou sem professor solicitado, como rede de segurança manual.
/// </summary>
public class CoordenadorController_NaoFiltraPorOrientadorSolicitado_Tests
{
    private const int IdAluno1 = 10;
    private const int IdAluno2 = 11;
    private const int IdProfessorSolicitado = 20;
    private const int IdOutroProfessor = 21;
    private const int IdCoordenador = 30;

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = true };

    [Fact]
    public async Task GetPropostasPendentes_ListaTantoComQuantoSemProfessorSolicitado()
    {
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno1, "Aluno Com Solicitação", "aluno1@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdAluno2, "Aluno Sem Solicitação", "aluno2@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessorSolicitado, "Professor", "prof@teste.com", TipoUsuario.Professor),
                NovoUsuario(IdCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));

            ctx.Tccs.AddRange(
                new Tcc { Titulo = "Com professor solicitado", Resumo = "Resumo 1", AlunoId = IdAluno1, OrientadorSolicitadoId = IdProfessorSolicitado, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow },
                new Tcc { Titulo = "Sem professor solicitado", Resumo = "Resumo 2", AlunoId = IdAluno2, OrientadorSolicitadoId = null, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/propostas-pendentes");

        response.EnsureSuccessStatusCode();
        var pendentes = await response.Content.ReadFromJsonAsync<PagedResult<TccResumoDto>>();

        Assert.NotNull(pendentes);
        Assert.Equal(2, pendentes!.Items.Count);
    }

    [Fact]
    public async Task DesignarOrientador_FuncionaMesmoQuandoOutroProfessorFoiOSolicitado()
    {
        // O Coordenador mantém autoridade para designar um professor DIFERENTE do que o aluno
        // solicitou — decisão de produto 4.2 (híbrido: quem decide primeiro vence).
        using var factory = new TccApiFactory();
        int tccId;
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno1, "Aluno", "aluno1@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessorSolicitado, "Professor Solicitado", "prof@teste.com", TipoUsuario.Professor),
                NovoUsuario(IdOutroProfessor, "Outro Professor", "outro@teste.com", TipoUsuario.Professor),
                NovoUsuario(IdCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));

            var tcc = new Tcc { Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno1, OrientadorSolicitadoId = IdProfessorSolicitado, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow };
            ctx.Tccs.Add(tcc);
            await ctx.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{tccId}/designar-orientador",
            new DesignarOrientadorDto { OrientadorId = IdOutroProfessor });

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tccAtualizado = await verifica.Tccs.SingleAsync(t => t.Id == tccId);
        Assert.Equal(StatusTcc.Aprovado, tccAtualizado.Status);
        // OrientadorSolicitadoId é histórico, nunca reescrito (D5 da arquitetura) — continua
        // apontando para quem o aluno pediu, mesmo que o Coordenador tenha designado outro.
        Assert.Equal(IdProfessorSolicitado, tccAtualizado.OrientadorSolicitadoId);
        Assert.Equal(IdOutroProfessor, tccAtualizado.OrientadorId);
    }
}
