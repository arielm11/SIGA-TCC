using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

public class CoordenadorController_AgendarBanca_Tests
{
    private const int idCoordenador  = 1;
    private const int idAluno = 10;
    private const int idProfessorOrientador = 20;
    private const int idProfessorAvaliador1 = 21;
    private const int idProfessorAvaliador2 = 22;

    private async Task<(TccApiFactory factory, int tccId)> PrepararCenarioComTccAguardandoDefesa()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Id = idAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Id = idProfessorOrientador, Nome = "Orientador Teste", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliador1 = new Usuario { Id = idProfessorAvaliador1, Nome = "Avaliador Um", Email = "avaliador1@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliador2 = new Usuario { Id = idProfessorAvaliador2, Nome = "Avaliador Dois", Email = "avaliador2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };

        context.Usuarios.AddRange(aluno, orientador, avaliador1, avaliador2);

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo de teste",
            AlunoId = idAluno,
            OrientadorId = idProfessorOrientador,
            Status = StatusTcc.AguardandoDefesa,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        return (factory, tcc.Id);
    }

    [Fact]
    public async Task Bug3_AgendarBanca_ComDoisAvaliadores_DeveCriarBancaComSucesso()
    {
        // Arrange
        var (factory, tccId) = await PrepararCenarioComTccAguardandoDefesa();
        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");

        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1, idProfessorAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var banca = await context.Banca
            .Include(b => b.Avaliadores)
            .FirstOrDefaultAsync(b => b.TccId == tccId);

        Assert.NotNull(banca);
        Assert.Equal("Sala 101", banca!.Local);
        Assert.Equal(2, banca.Avaliadores.Count);
    }

    [Fact]
    public async Task RN05_MenosDeDoisAvaliadores_DeveRetornarBadRequest()
    {
        // Arrange — valida a regra RN05 (mínimo 2 membros avaliadores)
        var (factory, tccId) = await PrepararCenarioComTccAguardandoDefesa();
        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");

        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1 }, // só 1 membro
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var bancaExiste = await context.Banca.AnyAsync(b => b.TccId == tccId);
        Assert.False(bancaExiste);
    }

    [Fact]
    public async Task TccComStatusErrado_DeveRetornarBadRequest()
    {
        // Arrange — TCC ainda em EmAndamento, não em AguardandoDefesa
        var factory = new TccApiFactory();
        using var contextSetup = factory.CriarContextoDireto();

        var aluno = new Usuario { Id = idAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        contextSetup.Usuarios.Add(aluno);

        var tcc = new Tcc
        {
            Titulo = "TCC Ainda em Andamento",
            Resumo = "Resumo",
            AlunoId = idAluno,
            Status = StatusTcc.EmAndamento, // ← status incompatível com agendamento de banca
            DataCriacao = DateTime.UtcNow
        };
        contextSetup.Tccs.Add(tcc);
        await contextSetup.SaveChangesAsync();

        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");
        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1, idProfessorAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tcc.Id}/banca", dto);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TccInexistente_DeveRetornarBadRequest()
    {
        // Arrange — nenhum TCC criado, idTcc não existe no banco
        var factory = new TccApiFactory();
        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");

        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1, idProfessorAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/coordenador/tcc/9999/banca", dto);

        // Assert — o controller trata "não encontrado" com a mesma mensagem
        // de status incompatível (tcc == null || tcc.Status != AguardandoDefesa),
        // então o retorno esperado é BadRequest, não NotFound
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ComCombinacaoMista_ProfessorEExterno_DeveContarAmbos()
    {
        // Arrange — valida que a soma ProfessoresIds + MembrosExternosIds
        // satisfaz a RN05, mesmo vindo de fontes diferentes
        var (factory, tccId) = await PrepararCenarioComTccAguardandoDefesa();
        using var contextSetup = factory.CriarContextoDireto();

        var membroExterno = new MembroExterno
        {
            Nome = "Avaliador Externo",
            Email = "externo@empresa.com",
            Instituicao = "Empresa Teste"
        };
        contextSetup.MembrosExternos.Add(membroExterno);
        await contextSetup.SaveChangesAsync();

        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");
        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1 }, // 1 professor
            MembrosExternosIds = new List<int> { membroExterno.Id }      // + 1 externo = 2 total
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var banca = await context.Banca
            .Include(b => b.Avaliadores)
            .FirstOrDefaultAsync(b => b.TccId == tccId);

        Assert.NotNull(banca);
        Assert.Equal(2, banca!.Avaliadores.Count);
        Assert.Contains(banca.Avaliadores, a => a.ProfessorId == idProfessorAvaliador1);
        Assert.Contains(banca.Avaliadores, a => a.MembroExternoId == membroExterno.Id);
    }

    [Fact]
    public async Task FusoHorario_DataHora_DeveSerSempreConvertidaComoHorarioDeBrasilia()
    {

        // Arrange
        var (factory, tccId) = await PrepararCenarioComTccAguardandoDefesa();
        var client = factory.CreateClientAutenticado(idCoordenador , "Coordenador");

        // 15/08/2026, 14:30 — horário "de Brasília" como o coordenador digitou na tela
        var dataHoraEmBrasilia = new DateTime(2026, 8, 15, 14, 30, 0, DateTimeKind.Unspecified);

        var dto = new AgendarBancaDto
        {
            DataHora = dataHoraEmBrasilia,
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idProfessorAvaliador1, idProfessorAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        // Act
        var response = await client.PostAsJsonAsync($"/api/coordenador/tcc/{tccId}/banca", dto);
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var banca = await context.Banca.FirstAsync(b => b.TccId == tccId);

        var utcEsperado = new DateTime(2026, 8, 15, 17, 30, 0, DateTimeKind.Utc);

        Assert.Equal(utcEsperado, banca.DataHora);
    }
}