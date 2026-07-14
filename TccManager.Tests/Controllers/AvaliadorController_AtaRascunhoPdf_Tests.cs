using System.Net;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração do endpoint GET /api/avaliador/banca/{idBanca}/ata-rascunho-pdf (RNF-01/Etapa 2).
/// A validação de vínculo é explícita: só o professor que é BancaAvaliador daquela banca
/// específica baixa o rascunho. Qualquer outro professor (inclusive o orientador, que não
/// tem vínculo BancaAvaliador — decisão 6) recebe 403 Forbidden, não bastando ter o papel
/// "Professor". Banca inexistente também cai em 403, pois sem vínculo o controller retorna
/// Forbid() antes de chegar à resolução de banca (não expõe existência).
/// </summary>
public class AvaliadorController_AtaRascunhoPdf_Tests
{
    private sealed record Semeadura(int BancaId, int AvaliadorId, int OrientadorId, int OutroProfId);

    private static async Task<Semeadura> SemearBancaAsync(
        WebRootIsolatedApiFactory factory,
        decimal? notaFinal = null,
        StatusTcc statusTcc = StatusTcc.AguardandoDefesa)
    {
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliador = new Usuario { Nome = "Avaliador Vinculado", Email = "aval@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var outroProf = new Usuario { Nome = "Prof de Outra Banca", Email = "outro@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador, avaliador, outroProf);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC Avaliador",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = statusTcc,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(2), Local = "Sala 1", NotaFinal = notaFinal };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = avaliador.Id });
        await context.SaveChangesAsync();

        return new Semeadura(banca.Id, avaliador.Id, orientador.Id, outroProf.Id);
    }

    [Fact]
    public async Task AvaliadorVinculado_RetornaPdf()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");

        var response = await client.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46,
            "O corpo da resposta não começa com a assinatura %PDF.");
    }

    [Fact]
    public async Task ProfessorNaoVinculado_Retorna403()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(s.OutroProfId, "Professor");

        var response = await client.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Orientador_MesmaBanca_Retorna403()
    {
        // O orientador tem papel Professor mas NÃO é BancaAvaliador — não deve acessar (decisão 6).
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(s.OrientadorId, "Professor");

        var response = await client.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BancaInexistente_Retorna403()
    {
        // Sem vínculo BancaAvaliador para a banca pedida (inexistente) → Forbid antes de 404.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");

        var response = await client.GetAsync("/api/avaliador/banca/9999/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NaoAutenticado_Retorna401()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory);
        var client = factory.CreateClient(); // sem cabeçalhos de auth

        var response = await client.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AvaliadorVinculado_ResultadoRegistrado_Retorna410()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaAsync(factory, notaFinal: 90m, statusTcc: StatusTcc.Finalizado);
        var client = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");

        var response = await client.GetAsync($"/api/avaliador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
