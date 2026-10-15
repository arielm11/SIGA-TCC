using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #112 (P9 da modelagem de dados) — UsuarioController.DeleteUsuario estende a
/// pré-checagem "possuiVinculos" (já existente para Tcc.OrientadorId e
/// BancaAvaliador.ProfessorId) para também cobrir Tcc.OrientadorSolicitadoId, sem filtrar por
/// Status — mesma política já aplicada a OrientadorId (mesmo se a proposta já foi
/// rejeitada/finalizada, o vínculo histórico impede a exclusão física).
/// </summary>
public class UsuarioController_DeleteUsuario_OrientadorSolicitado_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;
    private const int IdProfessorSolicitado = 20;

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = true };

    [Theory]
    [InlineData(StatusTcc.Pendente)]
    [InlineData(StatusTcc.Reprovado)]
    [InlineData(StatusTcc.Finalizado)]
    public async Task DeleteUsuario_ProfessorSolicitadoEmQualquerProposta_RetornaConflict_MesmoSemVinculoOperacional(StatusTcc status)
    {
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAdmin, "Admin", "admin@teste.com", TipoUsuario.Admin),
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessorSolicitado, "Professor Solicitado", "prof@teste.com", TipoUsuario.Professor));
            ctx.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorSolicitadoId = IdProfessorSolicitado,
                // OrientadorId nunca é preenchido pela solicitação (D5) — o professor não é
                // orientador operacional, só foi solicitado.
                OrientadorId = null,
                Status = status,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdProfessorSolicitado}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        Assert.True(await verifica.Usuarios.AnyAsync(u => u.Id == IdProfessorSolicitado));
    }

    [Fact]
    public async Task DeleteUsuario_ProfessorSemNenhumVinculo_ContinuaSendoExcluido()
    {
        // Contraprova: a extensão não deve bloquear exclusões legítimas de professores sem
        // nenhum histórico de vínculo.
        using var factory = new TccApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAdmin, "Admin", "admin@teste.com", TipoUsuario.Admin),
                NovoUsuario(IdProfessorSolicitado, "Professor Sem Vinculo", "prof@teste.com", TipoUsuario.Professor));
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdProfessorSolicitado}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        Assert.False(await verifica.Usuarios.AnyAsync(u => u.Id == IdProfessorSolicitado));
    }
}
