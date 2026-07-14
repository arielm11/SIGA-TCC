using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Regra RNF-03 (Etapa 2): uma vez que Banca.NotaFinal != null, os TRÊS pontos de acesso ao
/// rascunho (Coordenador, avaliador interno vinculado e público via token) devem responder
/// 410 Gone, independentemente de o token/banca já ter expirado por data. Cobre tanto o
/// cenário com a banca no futuro quanto o cenário realista pós-defesa (DataHora no passado).
/// RascunhoAtaTokenService.ValidarAsync checa NotaFinal antes da expiração por data para
/// garantir essa convergência.
/// </summary>
public class RascunhoAta_410Gone_Tests
{
    private const int idCoordenador = 1;

    private sealed record Semeadura(int BancaId, int AvaliadorId, int MembroExternoId);

    private static async Task<Semeadura> SemearBancaAsync(
        WebRootIsolatedApiFactory factory,
        DateTime dataHora,
        decimal notaFinal)
    {
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliador = new Usuario { Nome = "Avaliador", Email = "aval@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador, avaliador);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC 410",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = StatusTcc.Finalizado,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = dataHora, Local = "Sala 1", NotaFinal = notaFinal };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        var externo = new MembroExterno { Nome = "Externo", Email = "ext@empresa.com", Instituicao = "Empresa" };
        context.MembrosExternos.Add(externo);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = avaliador.Id });
        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = externo.Id });
        await context.SaveChangesAsync();

        return new Semeadura(banca.Id, avaliador.Id, externo.Id);
    }

    private static async Task<string> GerarTokenAsync(WebRootIsolatedApiFactory factory, int bancaId, int membroId)
    {
        using var scope = factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IRascunhoAtaTokenService>();
        return await tokenService.GerarTokenAsync(bancaId, membroId);
    }

    [Fact]
    public async Task ComResultado_BancaNoFuturo_TresPontosDeAcesso_Retornam410()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory, DateTime.UtcNow.AddDays(3), notaFinal: 88m);
        var token = await GerarTokenAsync(factory, s.BancaId, s.MembroExternoId);

        var coord = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var aval = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");
        var anon = factory.CreateClient();

        var respCoord = await coord.GetAsync($"/api/coordenador/banca/{s.BancaId}/ata-rascunho-pdf");
        var respAval = await aval.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");
        var respPublico = await anon.GetAsync($"/api/rascunho-ata/{token}");

        Assert.Equal(HttpStatusCode.Gone, respCoord.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, respAval.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, respPublico.StatusCode);
    }

    [Fact]
    public async Task ComResultado_PosDefesa_TresPontosDeAcesso_Retornam410()
    {
        // Cenário realista: resultado registrado após a defesa (DataHora no passado).
        // Os três pontos de acesso devem convergir em 410, não apenas Coordenador/avaliador.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory, DateTime.UtcNow.AddDays(-1), notaFinal: 88m);
        var token = await GerarTokenAsync(factory, s.BancaId, s.MembroExternoId);

        var coord = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var aval = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");
        var anon = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Gone, (await coord.GetAsync($"/api/coordenador/banca/{s.BancaId}/ata-rascunho-pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await aval.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync($"/api/rascunho-ata/{token}")).StatusCode);
    }
}
