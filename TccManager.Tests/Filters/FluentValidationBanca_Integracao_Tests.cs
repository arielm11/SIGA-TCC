using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Filters;

/// <summary>
/// Testes de integração do FluentValidationActionFilter através do pipeline HTTP real.
/// Foco: confirmar que uma falha de FluentValidation (data no passado) produz um 400 com
/// o mesmo formato ValidationProblemDetails (RFC 7807) que uma falha de DataAnnotations.
/// </summary>
public class FluentValidationBanca_Integracao_Tests
{
    private const int idCoordenador = 1;
    private const int idAluno = 10;
    private const int idOrientador = 20;
    private const int idAvaliador1 = 21;
    private const int idAvaliador2 = 22;

    private async Task<(TccApiFactory factory, int tccId)> PrepararTccAguardandoDefesa()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = idAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = idOrientador, Nome = "Orientador", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = idAvaliador1, Nome = "Avaliador 1", Email = "av1@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = idAvaliador2, Nome = "Avaliador 2", Email = "av2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = idAluno,
            OrientadorId = idOrientador,
            Status = StatusTcc.AguardandoDefesa,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        return (factory, tcc.Id);
    }

    [Fact]
    public async Task DataNoPassado_DeveRetornar400_ComValidationProblemDetails_ChaveDataHora()
    {
        // Arrange — DTO válido em tudo (Local preenchido, 2 avaliadores), exceto a data,
        // que está no passado — a única violação deve vir do FluentValidation.
        var (factory, tccId) = await PrepararTccAguardandoDefesa();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(-7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idAvaliador1, idAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);

        // Assert — status e content-type do padrão problem+details
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(nameof(AgendarBancaDto.DataHora), out var mensagens));
        Assert.Contains(
            "A data e hora da banca devem ser futuras.",
            mensagens.EnumerateArray().Select(m => m.GetString()));

        // Assert — o curto-circuito impediu qualquer persistência
        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Banca.AnyAsync(b => b.TccId == tccId));
    }

    [Fact]
    public async Task FormatoDeErro_FluentValidation_DeveSerIgualAoDeDataAnnotations()
    {
        // Arrange
        var (factory, tccId) = await PrepararTccAguardandoDefesa();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Falha de FluentValidation: data no passado, resto válido.
        var dtoFluent = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(-7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idAvaliador1, idAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Falha de DataAnnotations: Local vazio ([Required]), data no futuro (default) para
        // que o FluentValidation nem chegue a rodar — isola a 1ª camada.
        var dtoDataAnnotations = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "",
            ProfessoresIds = new List<int> { idAvaliador1, idAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var respFluent = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dtoFluent);
        var respDataAnnotations = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dtoDataAnnotations);

        // Assert — ambas 400 problem+json
        Assert.Equal(HttpStatusCode.BadRequest, respFluent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, respDataAnnotations.StatusCode);
        Assert.Equal("application/problem+json", respFluent.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/problem+json", respDataAnnotations.Content.Headers.ContentType?.MediaType);

        using var docFluent = JsonDocument.Parse(await respFluent.Content.ReadAsStringAsync());
        using var docData = JsonDocument.Parse(await respDataAnnotations.Content.ReadAsStringAsync());

        // Assert — mesmo conjunto de propriedades de topo (type, title, status, errors, traceId...)
        Assert.Equal(PropriedadesDeTopo(docFluent), PropriedadesDeTopo(docData));

        // Assert — mesmo title e status
        Assert.Equal(
            docData.RootElement.GetProperty("title").GetString(),
            docFluent.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            docData.RootElement.GetProperty("status").GetInt32(),
            docFluent.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(400, docFluent.RootElement.GetProperty("status").GetInt32());

        // Assert — a chave de erro corresponde ao nome da propriedade do DTO em cada caso
        Assert.True(docFluent.RootElement.GetProperty("errors")
            .TryGetProperty(nameof(AgendarBancaDto.DataHora), out _));
        Assert.True(docData.RootElement.GetProperty("errors")
            .TryGetProperty(nameof(AgendarBancaDto.Local), out _));
    }

    private static IEnumerable<string> PropriedadesDeTopo(JsonDocument doc) =>
        doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
}
