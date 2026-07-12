using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

public class Paginacao_Integracao_Tests
{
    private const int idCoordenador = 1;
    private const int idProfessor = 500;

    // ─────────────────────────── GET /api/coordenador/professores ───────────────────────────

    private static async Task<TccApiFactory> FactoryComProfessores(int quantidade)
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        for (int i = 1; i <= quantidade; i++)
        {
            context.Usuarios.Add(new Usuario
            {
                Id = i,
                Nome = $"Prof {i:D3}",
                Email = $"prof{i}@teste.com",
                SenhaHash = "x",
                Tipo = TipoUsuario.Professor,
                Ativo = true
            });
        }
        await context.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task GetProfessores_RetornaEnvelopePagedResult()
    {
        var factory = await FactoryComProfessores(3);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/professores");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(3, pagina!.TotalCount);
        Assert.Equal(1, pagina.TotalPages);
        Assert.Equal(1, pagina.CurrentPage);
        Assert.Equal(3, pagina.Items.Count);
    }

    [Fact]
    public async Task GetProfessores_TotalCountRefleteTotalReal_MesmoComPaginaParcial()
    {
        var factory = await FactoryComProfessores(25);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/professores?page=2&pageSize=10");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(25, pagina!.TotalCount);
        Assert.Equal(3, pagina.TotalPages);
        Assert.Equal(2, pagina.CurrentPage);
        Assert.Equal(10, pagina.Items.Count);
        // Ordenação por Nome mantida: página 2 começa em Prof 011.
        Assert.Equal("Prof 011", pagina.Items.First().Nome);
    }

    [Fact]
    public async Task GetProfessores_PageSizeAlto_RetornaTodos_CenarioDropdown()
    {
        var factory = await FactoryComProfessores(30);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/professores?pageSize={PaginacaoQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(30, pagina!.TotalCount);
        Assert.Equal(30, pagina.Items.Count);
        Assert.Equal(1, pagina.TotalPages);
    }

    [Fact]
    public async Task GetProfessores_ValoresNaoNumericos_NaoRetornam400_UsamDefaults()
    {
        var factory = await FactoryComProfessores(3);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/professores?page=abc&pageSize=xyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(PaginacaoQuery.DefaultPage, pagina!.CurrentPage);
        Assert.Equal(PaginacaoQuery.DefaultPageSize, pagina.PageSize);
        Assert.Equal(3, pagina.TotalCount);
    }

    [Fact]
    public async Task GetProfessores_PageMenorQueUm_ClampaParaUm_SemErro()
    {
        var factory = await FactoryComProfessores(5);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/professores?page=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(1, pagina!.CurrentPage);
    }

    [Fact]
    public async Task GetProfessores_PageSizeAcimaDoMaximo_ClampaPara100()
    {
        var factory = await FactoryComProfessores(3);
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/professores?pageSize=9999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(PaginacaoQuery.MaxPageSize, pagina!.PageSize);
    }

    // ─────────────────────────── GET /api/orientador/dashboard ───────────────────────────

    private static async Task<TccApiFactory> FactoryComPropostasPendentes(int quantidade)
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = idProfessor,
            Nome = "Professor Orientador",
            Email = "orientador@teste.com",
            SenhaHash = "x",
            Tipo = TipoUsuario.Professor,
            Ativo = true
        });

        for (int i = 1; i <= quantidade; i++)
        {
            var alunoId = 1000 + i;
            context.Usuarios.Add(new Usuario
            {
                Id = alunoId,
                Nome = $"Aluno {i:D3}",
                Email = $"aluno{i}@teste.com",
                SenhaHash = "x",
                Tipo = TipoUsuario.Aluno,
                Ativo = true
            });
            context.Tccs.Add(new Tcc
            {
                Titulo = $"Proposta {i:D3}",
                Resumo = "Resumo",
                AlunoId = alunoId,
                Status = StatusTcc.Pendente,
                DataCriacao = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)
            });
        }
        await context.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task GetDashboard_PropostasPendentes_RetornaEnvelopePagedResult()
    {
        var factory = await FactoryComPropostasPendentes(5);
        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.GetAsync("/api/orientador/dashboard");

        response.EnsureSuccessStatusCode();
        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        Assert.NotNull(dashboard);
        Assert.NotNull(dashboard!.PropostasPendentes);
        Assert.Equal(5, dashboard.PropostasPendentes.TotalCount);
        Assert.Equal(1, dashboard.PropostasPendentes.CurrentPage);
        Assert.Equal(5, dashboard.PropostasPendentes.Items.Count);
    }

    [Fact]
    public async Task GetDashboard_PropostasPendentes_TotalCountRefleteTotal_ComPaginaParcial()
    {
        var factory = await FactoryComPropostasPendentes(25);
        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.GetAsync("/api/orientador/dashboard?page=2&pageSize=10");

        response.EnsureSuccessStatusCode();
        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        Assert.NotNull(dashboard);
        var pendentes = dashboard!.PropostasPendentes;
        Assert.Equal(25, pendentes.TotalCount);
        Assert.Equal(3, pendentes.TotalPages);
        Assert.Equal(2, pendentes.CurrentPage);
        Assert.Equal(10, pendentes.Items.Count);
    }

    [Fact]
    public async Task GetDashboard_PropostasPendentes_OrdenadasPorDataCriacaoDescendente()
    {
        var factory = await FactoryComPropostasPendentes(5);
        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.GetAsync("/api/orientador/dashboard");

        response.EnsureSuccessStatusCode();
        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        var itens = dashboard!.PropostasPendentes.Items;
        // Proposta 005 tem a maior DataCriacao (mais recente) => vem primeiro.
        Assert.Equal("Proposta 005", itens.First().Titulo);
        var datas = itens.Select(i => i.DataCriacao).ToList();
        Assert.Equal(datas.OrderByDescending(d => d).ToList(), datas);
    }

    [Fact]
    public async Task GetDashboard_PageSizeAlto_Funciona()
    {
        var factory = await FactoryComPropostasPendentes(30);
        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.GetAsync($"/api/orientador/dashboard?pageSize={PaginacaoQuery.MaxPageSize}");

        response.EnsureSuccessStatusCode();
        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        Assert.Equal(30, dashboard!.PropostasPendentes.TotalCount);
        Assert.Equal(30, dashboard.PropostasPendentes.Items.Count);
    }

    [Fact]
    public async Task GetDashboard_ValoresNaoNumericos_NaoRetornam400()
    {
        var factory = await FactoryComPropostasPendentes(3);
        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.GetAsync("/api/orientador/dashboard?page=foo&pageSize=bar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dashboard = await response.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        Assert.Equal(PaginacaoQuery.DefaultPage, dashboard!.PropostasPendentes.CurrentPage);
        Assert.Equal(PaginacaoQuery.DefaultPageSize, dashboard.PropostasPendentes.PageSize);
    }
}
