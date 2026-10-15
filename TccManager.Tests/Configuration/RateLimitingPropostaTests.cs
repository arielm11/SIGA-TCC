using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #141 (achado M4) — política de rate limiting "proposta", aplicada a
/// POST /api/tcc/proposta e DELETE /api/tcc/proposta/{id}. FixedWindow, PermitLimit
/// 10/3600s, particionado por usuário autenticado (mesmo raciocínio de
/// "upload"/"geracao-pdf"/"listagem-paginada", achado A02-2). A cota é COMPARTILHADA entre
/// os dois endpoints (mesma partição "user:{id}") — é exatamente o cenário de abuso real
/// (loop submeter → excluir) que a issue descreve.
///
/// Como em RateLimitingUploadTests, UseRateLimiter roda antes do endpoint: a cota é
/// consumida mesmo quando a requisição falha depois por outro motivo de negócio.
/// </summary>
public class RateLimitingPropostaTests
{
    private const int PermitLimit = 10;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;

    private static readonly PropostaTccDto PropostaValida = new()
    {
        Titulo = "Título de Teste",
        Resumo = "Resumo de teste com conteúdo suficiente para passar na validação."
    };

    private static async Task<TccApiFactory> CriarFactoryComAlunoAsync(params int[] idsAlunos)
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        foreach (var id in idsAlunos)
        {
            context.Usuarios.Add(new Usuario { Id = id, Nome = $"Aluno {id}", Email = $"aluno{id}@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
        }

        await context.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task SubmeterProposta_AcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = await CriarFactoryComAlunoAsync(IdAluno);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resposta = await client.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueada = await client.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
    }

    [Fact]
    public async Task ExcluirProposta_AcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = await CriarFactoryComAlunoAsync(IdAluno);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        const int idPropostaInexistente = 9999;

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resposta = await client.DeleteAsync($"/api/tcc/proposta/{idPropostaInexistente}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueada = await client.DeleteAsync($"/api/tcc/proposta/{idPropostaInexistente}");

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
    }

    [Fact]
    public async Task CotaECompartilhadaEntreSubmeterEExcluir()
    {
        // Núcleo do achado M4: o ciclo de abuso alterna submeter/excluir — a cota precisa
        // valer para a SOMA das duas chamadas, não uma janela independente para cada uma.
        using var factory = await CriarFactoryComAlunoAsync(IdAluno);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        const int idPropostaInexistente = 9999;

        // 1 submissão (cria o Tcc Pendente) + 9 exclusões de um id inexistente = 10 chamadas.
        await client.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);
        for (var i = 1; i < PermitLimit; i++)
        {
            var resposta = await client.DeleteAsync($"/api/tcc/proposta/{idPropostaInexistente}");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueada = await client.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
    }

    [Fact]
    public async Task CotaNaoEhCompartilhadaEntreUsuariosDiferentes()
    {
        using var factory = await CriarFactoryComAlunoAsync(IdAluno, IdOutroAluno);

        var alunoA = factory.CreateClientAutenticado(IdAluno, "Aluno");
        for (var i = 1; i <= PermitLimit; i++)
        {
            await alunoA.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);
        }

        var bloqueadaParaA = await alunoA.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueadaParaA.StatusCode);

        var alunoB = factory.CreateClientAutenticado(IdOutroAluno, "Aluno");
        var respostaParaB = await alunoB.PostAsJsonAsync("/api/tcc/proposta", PropostaValida);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, respostaParaB.StatusCode);
    }
}
