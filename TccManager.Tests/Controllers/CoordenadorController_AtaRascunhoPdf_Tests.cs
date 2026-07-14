using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TccManager.Api.Services.Email;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração dos endpoints do Coordenador do rascunho (Etapa 2):
/// GET /api/coordenador/banca/{idBanca}/ata-rascunho-pdf (download, 200/404/410) e
/// POST /api/coordenador/banca/{idBanca}/membro-externo/{idMembroExterno}/reenviar-rascunho
/// (revoga o token vigente — não deleta — gera um novo, e enfileira o e-mail dedicado).
/// Usa FakeEmailQueue para capturar o e-mail de reenvio e extrair o novo token, validando
/// ponta a ponta que o antigo passa a 404 e o novo a 200 no endpoint público.
/// </summary>
public class CoordenadorController_AtaRascunhoPdf_Tests
{
    private const int idCoordenador = 1;

    private sealed class FactoryComFilaFake : WebRootIsolatedApiFactory
    {
        private readonly FakeEmailQueue _fila;
        public FactoryComFilaFake(FakeEmailQueue fila) => _fila = fila;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(_fila);
            });
        }
    }

    private sealed record Semeadura(int BancaId, int MembroExternoId);

    private static async Task<Semeadura> SemearBancaComMembroAsync(
        WebRootIsolatedApiFactory factory,
        decimal? notaFinal = null,
        StatusTcc statusTcc = StatusTcc.AguardandoDefesa)
    {
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC Coordenador",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = statusTcc,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1", NotaFinal = notaFinal };
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

    private static string ExtrairToken(string corpoHtml)
    {
        var match = Regex.Match(corpoHtml, "/api/rascunho-ata/([0-9a-f]{64})");
        Assert.True(match.Success, "O corpo do e-mail de reenvio não contém um link com token de 64 hex.");
        return match.Groups[1].Value;
    }

    // ── Download pelo Coordenador ─────────────────────────────────────

    [Fact]
    public async Task Download_BancaSemResultado_RetornaPdf()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaComMembroAsync(factory);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Download_BancaInexistente_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/banca/9999/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_ResultadoRegistrado_Retorna410()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearBancaComMembroAsync(factory, notaFinal: 90m, statusTcc: StatusTcc.Finalizado);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{s.BancaId}/ata-rascunho-pdf");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    // ── Reenvio (RF-06) ───────────────────────────────────────────────

    [Fact]
    public async Task Reenvio_RevogaTokenAntigo_EGeraNovoValido_EnfileirandoEmail()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);
        var s = await SemearBancaComMembroAsync(factory);

        // Token inicial (simula o emitido no agendamento) — válido no público antes do reenvio.
        var tokenAntigo = await GerarTokenAsync(factory, s.BancaId, s.MembroExternoId);
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/rascunho-ata/{tokenAntigo}")).StatusCode);

        // Reenvio pelo Coordenador.
        var coord = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var reenvio = await coord.PostAsync(
            $"/api/coordenador/banca/{s.BancaId}/membro-externo/{s.MembroExternoId}/reenviar-rascunho", null);
        Assert.Equal(HttpStatusCode.OK, reenvio.StatusCode);

        // E-mail dedicado enfileirado para o membro externo.
        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Novo link de acesso ao rascunho da ata", msg.Assunto);
        Assert.Equal(new[] { "ext@empresa.com" }, msg.Destinatarios);

        // Token antigo passa a inválido (404) imediatamente; o novo (extraído do e-mail) valida (200).
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/rascunho-ata/{tokenAntigo}")).StatusCode);

        var tokenNovo = ExtrairToken(msg.CorpoHtml);
        Assert.NotEqual(tokenAntigo, tokenNovo);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/rascunho-ata/{tokenNovo}")).StatusCode);

        // O token antigo foi REVOGADO, não deletado: histórico preservado, exatamente 1 ativo.
        using var context = factory.CriarContextoDireto();
        var linhas = context.RascunhoAtaTokens
            .Where(t => t.BancaId == s.BancaId && t.MembroExternoId == s.MembroExternoId)
            .ToList();
        Assert.Equal(2, linhas.Count);
        Assert.Single(linhas, t => t.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Reenvio_MembroNaoAvaliadorDaBanca_Retorna404()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);
        var s = await SemearBancaComMembroAsync(factory);
        var coord = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Membro externo inexistente / não vinculado a esta banca.
        var response = await coord.PostAsync(
            $"/api/coordenador/banca/{s.BancaId}/membro-externo/999999/reenviar-rascunho", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task Reenvio_ResultadoJaRegistrado_Retorna410()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);
        var s = await SemearBancaComMembroAsync(factory, notaFinal: 85m, statusTcc: StatusTcc.Finalizado);
        var coord = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await coord.PostAsync(
            $"/api/coordenador/banca/{s.BancaId}/membro-externo/{s.MembroExternoId}/reenviar-rascunho", null);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }
}
