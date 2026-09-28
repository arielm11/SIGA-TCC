using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #120: <c>DarAceiteFinal</c> validava o vínculo com o TCC e a existência de uma Entrega
/// Final aprovada, mas não verificava <c>Tcc.Status</c> — um TCC já <c>Finalizado</c>/
/// <c>Reprovado</c> (a Entrega Final aprovada continua lá) podia ser "rearmado" de volta para
/// <c>AguardandoDefesa</c>, reabrindo caminho para sobrescrever a nota final/ata já registradas
/// em <c>RegistrarResultadoBanca</c>. Mesma guarda dupla (Aprovado/EmAndamento) já usada nos
/// outros pontos do sistema.
/// </summary>
public class OrientadorController_DarAceiteFinal_GuardaDeStatus_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private static async Task<int> SemearTccComEntregaFinalAprovadaAsync(
        WebRootIsolatedApiFactory factory, StatusTcc statusTcc)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdOrientador,
            Status = statusTcc,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        // A Entrega Final aprovada continua existindo mesmo depois de Finalizado/Reprovado —
        // é exatamente essa persistência que permitia o rearme antes do fix.
        context.Entregas.Add(new Entrega
        {
            TccId = tcc.Id,
            Titulo = "Versão Final",
            ArquivoCaminho = "/uploads/entregas/final.pdf",
            Tipo = TipoEntrega.Final,
            Status = StatusEntrega.Aprovada,
            DataEnvio = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return tcc.Id;
    }

    [Theory]
    [InlineData(StatusTcc.Finalizado)]
    [InlineData(StatusTcc.Reprovado)]
    public async Task TccJaConcluido_Retorna400ENaoRearmaParaAguardandoDefesa(StatusTcc statusConcluido)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccComEntregaFinalAprovadaAsync(factory, statusConcluido);
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Tccs.FindAsync(tccId);
        Assert.Equal(statusConcluido, persistido!.Status); // NÃO rearmou para AguardandoDefesa
    }

    [Theory]
    [InlineData(StatusTcc.Aprovado)]
    [InlineData(StatusTcc.EmAndamento)]
    public async Task TccEmAndamentoDeOrientacao_ContinuaAceitandoNormalmente(StatusTcc statusValido)
    {
        // Contraprova: a guarda nova não pode afetar o caminho legítimo já coberto por
        // OrientadorController_CicloVeredictoFinal_Tests.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccComEntregaFinalAprovadaAsync(factory, statusValido);
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(StatusTcc.AguardandoDefesa, (await context.Tccs.FindAsync(tccId))!.Status);
    }

    [Fact]
    public async Task TccPendente_SemOrientadorDesignado_Retorna400()
    {
        // Estado que, na prática, o vínculo (OrientadorId == profId) já bloquearia (NotFound) —
        // mas se o TCC estivesse Pendente com orientador já designado por algum motivo, a
        // guarda nova também precisa recusar.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccComEntregaFinalAprovadaAsync(factory, StatusTcc.Pendente);
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
