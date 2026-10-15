using System.Net;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #97 (achado A06-1) — política de rate limiting "upload", aplicada a
/// POST /api/tcc/entregas e POST /coordenador/banca/{id}/registrar-resultado. FixedWindow,
/// PermitLimit 10/3600s, particionado por usuário autenticado (mesmo raciocínio de
/// "geracao-pdf"/"listagem-paginada", achado A02-2 — ver RateLimitingGeracaoPdfTests).
///
/// Como em RateLimitingGeracaoPdfTests, UseRateLimiter roda antes de UseAuthorization/endpoint:
/// a cota é consumida mesmo quando a requisição depois falha por outro motivo de negócio (sem
/// TCC aprovado, banca inexistente) — os testes exploram isso para não precisar montar um
/// upload multipart de verdade em cada uma das 11 chamadas.
/// </summary>
public class RateLimitingUploadTests
{
    private const int PermitLimit = 10;
    private const int IdAlunoSemTcc = 10;
    private const int IdCoordenador = 1;

    [Fact]
    public async Task EnviarEntrega_AcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAlunoSemTcc, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAlunoSemTcc, "Aluno");

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resposta = await client.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueada = await client.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
    }

    [Fact]
    public async Task EnviarEntrega_DentroDoLimite_NenhumaEhBloqueada()
    {
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAlunoSemTcc, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAlunoSemTcc, "Aluno");

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resposta = await client.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode); // "TCC precisa estar aprovado"
        }
    }

    [Fact]
    public async Task RegistrarResultadoBanca_AcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        const int idBancaInexistente = 9999;

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resposta = await client.PostAsync(
                $"/api/coordenador/banca/{idBancaInexistente}/registrar-resultado", new MultipartFormDataContent());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueada = await client.PostAsync(
            $"/api/coordenador/banca/{idBancaInexistente}/registrar-resultado", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
    }

    [Fact]
    public async Task CotaNaoEhCompartilhadaEntreUsuariosDiferentes()
    {
        // Mesmo núcleo do achado A02-2 já coberto para as outras políticas particionadas por
        // usuário: esgotar a cota de um Aluno não pode afetar outro.
        const int idOutroAluno = 11;
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAlunoSemTcc, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = idOutroAluno, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var alunoA = factory.CreateClientAutenticado(IdAlunoSemTcc, "Aluno");
        for (var i = 1; i <= PermitLimit; i++)
        {
            await alunoA.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());
        }

        var bloqueadaParaA = await alunoA.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueadaParaA.StatusCode);

        var alunoB = factory.CreateClientAutenticado(idOutroAluno, "Aluno");
        var respostaParaB = await alunoB.PostAsync("/api/tcc/entregas", new MultipartFormDataContent());

        Assert.NotEqual(HttpStatusCode.TooManyRequests, respostaParaB.StatusCode);
    }
}
