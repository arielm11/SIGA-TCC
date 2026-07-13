using System.Net;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração do endpoint GET /api/coordenador/banca/{idBanca}/ata-pdf através do pipeline
/// HTTP real (WebApplicationFactory + EF InMemory). Cobre os três desfechos documentados em
/// docs/implementacao/2026-07-13-pdf-ata-questpdf.md: 404 (banca inexistente), 409 (resultado
/// não registrado) e 200 (application/pdf com corpo iniciando em %PDF), incluindo aprovação,
/// reprovação com motivo e uma banca com avaliador externo na composição.
/// </summary>
public class CoordenadorController_AtaPdf_Tests
{
    private const int idCoordenador = 1;

    private static async Task<(WebRootIsolatedApiFactory factory, int bancaId)> SemearBancaAsync(
        decimal? notaFinal,
        StatusTcc statusTcc,
        string? motivoRejeicao = null,
        bool comAvaliadorExterno = false)
    {
        var factory = new WebRootIsolatedApiFactory();
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno Ata", Email = "aluno.ata@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Prof. Orientador", Email = "orient.ata@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliadorInterno = new Usuario { Nome = "Prof. Avaliador", Email = "aval.ata@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador, avaliadorInterno);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC para Ata em PDF",
            Resumo = "Resumo",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = statusTcc,
            MotivoRejeicao = motivoRejeicao,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca
        {
            TccId = tcc.Id,
            DataHora = DateTime.UtcNow.AddDays(-1),
            Local = "Auditório Central",
            NotaFinal = notaFinal
        };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = avaliadorInterno.Id });

        if (comAvaliadorExterno)
        {
            var externo = new MembroExterno { Nome = "Dra. Convidada Externa", Email = "convidada@outra.edu", Instituicao = "Universidade Parceira" };
            context.MembrosExternos.Add(externo);
            await context.SaveChangesAsync();
            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = externo.Id });
        }

        await context.SaveChangesAsync();
        return (factory, banca.Id);
    }

    private static async Task AssertPdfValidoAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100, "PDF retornado é suspeito de estar vazio/truncado.");
        Assert.True(
            bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46,
            "O corpo da resposta não começa com a assinatura %PDF.");
    }

    [Fact]
    public async Task BancaInexistente_RetornaNotFound()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/banca/9999/ata-pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BancaSemResultadoRegistrado_RetornaConflict()
    {
        var (factory, bancaId) = await SemearBancaAsync(notaFinal: null, statusTcc: StatusTcc.AguardandoDefesa);
        using var _ = factory;
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task BancaAprovada_RetornaPdf()
    {
        var (factory, bancaId) = await SemearBancaAsync(notaFinal: 90.0m, statusTcc: StatusTcc.Finalizado);
        using var _ = factory;
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        await AssertPdfValidoAsync(response);
    }

    [Fact]
    public async Task BancaAprovada_ComAvaliadorExterno_RetornaPdf()
    {
        var (factory, bancaId) = await SemearBancaAsync(
            notaFinal: 88.0m, statusTcc: StatusTcc.Finalizado, comAvaliadorExterno: true);
        using var _ = factory;
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        await AssertPdfValidoAsync(response);
    }

    [Fact]
    public async Task BancaReprovada_ComMotivo_RetornaPdf()
    {
        var (factory, bancaId) = await SemearBancaAsync(
            notaFinal: 45.0m, statusTcc: StatusTcc.Reprovado,
            motivoRejeicao: "Metodologia insuficiente para os objetivos propostos.",
            comAvaliadorExterno: true);
        using var _ = factory;
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        await AssertPdfValidoAsync(response);
    }
}
