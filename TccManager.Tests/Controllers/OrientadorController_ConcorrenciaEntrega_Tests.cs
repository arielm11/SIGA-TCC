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
/// Issue #145 (achado B2): Entrega passou a ter RowVersion ([Timestamp]) — estes testes
/// cobrem o tratamento de 409 em RegistrarFeedback/AprovarEntrega/RejeitarEntrega e a nova
/// guarda de status em RegistrarFeedback (antes inexistente, diferente de
/// AprovarEntrega/RejeitarEntrega que já tinham desde a #81).
/// </summary>
public class OrientadorController_ConcorrenciaEntrega_Tests
{
    private const int IdProfessor = 20;
    private const int IdAluno = 10;

    private static async Task<(TccApiFactory factory, int entregaId)> PrepararCenarioAsync(TccApiFactory factory, StatusTcc statusTcc = StatusTcc.EmAndamento)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor,
            Status = statusTcc, DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var entrega = new Entrega
        {
            TccId = tcc.Id, Titulo = "Parcial", ArquivoCaminho = "/uploads/entregas/x.pdf",
            Tipo = TipoEntrega.Parcial, DataEnvio = DateTime.UtcNow
        };
        context.Entregas.Add(entrega);
        await context.SaveChangesAsync();

        return (factory, entrega.Id);
    }

    [Fact]
    public async Task RegistrarFeedback_ConflitoDeConcorrencia_Retorna409()
    {
        var factory = new SaveChangesFalhaConcorrenciaEntregaApiFactory();
        var (_, entregaId) = await PrepararCenarioAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var response = await client.PostAsJsonAsync($"/api/orientador/entregas/{entregaId}/feedback", new FeedbackDto { Feedback = "Bom trabalho.", Nota = 8m });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AprovarEntrega_ConflitoDeConcorrencia_Retorna409()
    {
        var factory = new SaveChangesFalhaConcorrenciaEntregaApiFactory();
        var (_, entregaId) = await PrepararCenarioAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var response = await client.PostAsync($"/api/orientador/entregas/{entregaId}/aprovar", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RejeitarEntrega_ConflitoDeConcorrencia_Retorna409()
    {
        var factory = new SaveChangesFalhaConcorrenciaEntregaApiFactory();
        var (_, entregaId) = await PrepararCenarioAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var response = await client.PostAsJsonAsync($"/api/orientador/entregas/{entregaId}/rejeitar", new RejeicaoDto { Motivo = "Motivo qualquer." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    [InlineData(StatusTcc.Reprovado)]
    [InlineData(StatusTcc.Pendente)]
    public async Task RegistrarFeedback_TccForaDeAcompanhamento_Retorna400ENaoAltera(StatusTcc status)
    {
        // Issue #145: guarda nova — antes era possível editar nota/parecer mesmo com o TCC
        // já Finalizado/Reprovado/etc., sem nenhuma checagem de status.
        using var factory = new TccApiFactory();
        var (_, entregaId) = await PrepararCenarioAsync(factory, status);
        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var response = await client.PostAsJsonAsync($"/api/orientador/entregas/{entregaId}/feedback", new FeedbackDto { Feedback = "Tentativa tardia.", Nota = 5m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var entrega = await context.Entregas.FindAsync(entregaId);
        Assert.Null(entrega!.Feedback);
    }

    [Theory]
    [InlineData(StatusTcc.Aprovado)]
    [InlineData(StatusTcc.EmAndamento)]
    public async Task RegistrarFeedback_TccEmAcompanhamento_Permite(StatusTcc status)
    {
        using var factory = new TccApiFactory();
        var (_, entregaId) = await PrepararCenarioAsync(factory, status);
        var client = factory.CreateClientAutenticado(IdProfessor, "Professor");

        var response = await client.PostAsJsonAsync($"/api/orientador/entregas/{entregaId}/feedback", new FeedbackDto { Feedback = "Ótimo.", Nota = 9m });

        response.EnsureSuccessStatusCode();
    }
}
