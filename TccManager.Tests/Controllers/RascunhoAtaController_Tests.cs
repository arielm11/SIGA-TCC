using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração do endpoint público GET /api/rascunho-ata/{token} através do pipeline HTTP
/// (WebApplicationFactory + EF InMemory). Cobre: [AllowAnonymous] efetivo (sem cabeçalho de
/// auth), token válido → 200 application/pdf (%PDF), token inexistente/revogado/expirado →
/// 404 genérico, e ResultadoRegistrado → 410 Gone.
/// </summary>
public class RascunhoAtaController_Tests
{
    private sealed record Semeadura(int BancaId, int MembroExternoId);

    private static async Task<Semeadura> SemearBancaComMembroAsync(
        WebRootIsolatedApiFactory factory,
        DateTime dataHora,
        decimal? notaFinal = null)
    {
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC Público",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = notaFinal == null ? StatusTcc.AguardandoDefesa : StatusTcc.Finalizado,
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

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = externo.Id });
        await context.SaveChangesAsync();

        return new Semeadura(banca.Id, externo.Id);
    }

    private static async Task<string> GerarTokenAsync(WebRootIsolatedApiFactory factory, int bancaId, int membroId)
    {
        using var scope = factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IRascunhoAtaTokenService>();
        return await tokenService.GerarTokenAsync(bancaId, membroId);
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
    public async Task TokenValido_SemAutenticacao_RetornaPdf()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var semeadura = await SemearBancaComMembroAsync(factory, DateTime.UtcNow.AddDays(3));
        var token = await GerarTokenAsync(factory, semeadura.BancaId, semeadura.MembroExternoId);

        // CreateClient() puro: nenhum cabeçalho X-Test-UserId → prova o [AllowAnonymous].
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/rascunho-ata/{token}");

        await AssertPdfValidoAsync(response);
    }

    [Fact]
    public async Task TokenInexistente_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/rascunho-ata/{new string('0', 64)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TokenRevogado_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var semeadura = await SemearBancaComMembroAsync(factory, DateTime.UtcNow.AddDays(3));
        var token = await GerarTokenAsync(factory, semeadura.BancaId, semeadura.MembroExternoId);

        using (var scope = factory.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<IRascunhoAtaTokenService>();
            await tokenService.RevogarTokenAtualAsync(semeadura.BancaId, semeadura.MembroExternoId);
        }

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/rascunho-ata/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TokenExpiradoPorData_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var semeadura = await SemearBancaComMembroAsync(factory, DateTime.UtcNow.AddMinutes(-10));
        var token = await GerarTokenAsync(factory, semeadura.BancaId, semeadura.MembroExternoId);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/rascunho-ata/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResultadoRegistrado_ComBancaNoFuturo_Retorna410()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var semeadura = await SemearBancaComMembroAsync(factory, DateTime.UtcNow.AddDays(3), notaFinal: 88m);
        var token = await GerarTokenAsync(factory, semeadura.BancaId, semeadura.MembroExternoId);

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/rascunho-ata/{token}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
