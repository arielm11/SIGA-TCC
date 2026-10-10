using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #144 (achados A3/A4/B7/B8): GetDetalhesTcc, GetMeuTcc, SubmeterProposta,
/// GetMinhasEntregas, EnviarEntrega e GetAcompanhamentos devolviam a entidade EF Core crua —
/// mesma classe de risco do vazamento de Usuario.SenhaHash corrigido na #137. Estes testes
/// provam, no payload bruto, que os campos irrelevantes/sensíveis que só existiam por essa
/// entidade crua (PrecisaTrocarSenha/LimiteOrientandos/AceitandoOrientandos/Ativo do Aluno,
/// ArquivoCaminho interno de armazenamento) não aparecem mais, e que os campos projetados
/// (nomeAluno, extensaoArquivo) aparecem no formato esperado.
/// </summary>
public class EntidadeCrua_ProjetadaParaDto_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private static async Task<(TccApiFactory factory, int tccId, int entregaId)> PrepararCenarioAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true, PrecisaTrocarSenha = false },
            new Usuario { Id = IdOrientador, Nome = "Orientador Teste", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true, LimiteOrientandos = 7, AceitandoOrientandos = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdOrientador,
            Status = StatusTcc.EmAndamento,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var entrega = new Entrega
        {
            TccId = tcc.Id,
            Titulo = "Versão Parcial",
            ArquivoCaminho = "/uploads/entregas/abc123-nome-do-aluno-confidencial.pdf",
            Tipo = TipoEntrega.Parcial,
            DataEnvio = DateTime.UtcNow
        };
        context.Entregas.Add(entrega);
        await context.SaveChangesAsync();

        return (factory, tcc.Id, entrega.Id);
    }

    [Fact]
    public async Task GetDetalhesTcc_PayloadBruto_NaoContemCamposIrrelevantesDoAluno()
    {
        var (factory, tccId, _) = await PrepararCenarioAsync();
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.GetAsync($"/api/orientador/tcc/{tccId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("precisaTrocarSenha", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limiteOrientandos", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aceitandoOrientandos", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arquivoCaminho", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidencial", corpo, StringComparison.OrdinalIgnoreCase);

        var dto = await response.Content.ReadFromJsonAsync<TccDetalheDto>();
        Assert.Equal("Aluno Teste", dto!.NomeAluno);
        Assert.Equal(".pdf", dto.Entregas.Single().ExtensaoArquivo);
    }

    [Fact]
    public async Task GetMeuTcc_PayloadBruto_NaoContemArquivoCaminho()
    {
        var (factory, _, _) = await PrepararCenarioAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/meu-tcc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("arquivoCaminho", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidencial", corpo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMinhasEntregas_PayloadBruto_NaoContemArquivoCaminhoMasContemExtensao()
    {
        var (factory, _, _) = await PrepararCenarioAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/entregas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("arquivoCaminho", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidencial", corpo, StringComparison.OrdinalIgnoreCase);

        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<EntregaDto>>();
        Assert.Equal(".pdf", pagina!.Items.Single().ExtensaoArquivo);
    }

    [Fact]
    public async Task EnviarEntrega_RespostaDeCriacao_NaoContemArquivoCaminho()
    {
        var (factory, tccId, _) = await PrepararCenarioAsync();
        using (var context = factory.CriarContextoDireto())
        {
            var tcc = await context.Tccs.FindAsync(tccId);
            tcc!.Status = StatusTcc.Aprovado;
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nova Entrega"), "tituloEntrega" },
            { new StringContent(TipoEntrega.Parcial.ToString()), "tipo" },
            { new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nconteudo")), "arquivo", "trabalho-confidencial.pdf" }
        };

        var response = await client.PostAsync("/api/tcc/entregas", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("arquivoCaminho", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trabalho-confidencial", corpo, StringComparison.OrdinalIgnoreCase);

        var dto = await response.Content.ReadFromJsonAsync<EntregaDto>();
        Assert.Equal(".pdf", dto!.ExtensaoArquivo);
    }

    [Fact]
    public async Task GetAcompanhamentos_PayloadBruto_NaoContemCampoTcc()
    {
        var (factory, tccId, _) = await PrepararCenarioAsync();
        using (var context = factory.CriarContextoDireto())
        {
            context.Acompanhamentos.Add(new Acompanhamento { TccId = tccId, DataReuniao = DateTime.UtcNow, Ata = "Reunião de teste" });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var response = await client.GetAsync("/api/tcc/acompanhamentos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        // Guarda contra ciclo/fix-up de navegação: o campo "tcc" (de volta para o pai) nunca
        // deveria existir na projeção.
        Assert.DoesNotContain("\"tcc\":", corpo, StringComparison.OrdinalIgnoreCase);

        var lista = await response.Content.ReadFromJsonAsync<List<AcompanhamentoResumoDto>>();
        Assert.Equal("Reunião de teste", lista!.Single().Ata);
    }

    [Fact]
    public async Task SubmeterProposta_RespostaDeCriacao_NaoContemArquivoCaminho()
    {
        using var factory = new TccApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno Novo", Email = "aluno-novo@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var dto = new PropostaTccDto { Titulo = "Título", Resumo = "Resumo detalhado da proposta de teste." };

        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("arquivoCaminho", corpo, StringComparison.OrdinalIgnoreCase);

        var retornado = await response.Content.ReadFromJsonAsync<TccDetalheDto>();
        Assert.Equal("Título", retornado!.Titulo);
        Assert.Empty(retornado.Entregas);
    }
}
