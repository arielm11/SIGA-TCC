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
    //
    // Issue #76 (D2/D3): os 5 testes que existiam aqui cobriam a paginação de
    // DashboardOrientadorDto.PropostasPendentes — contrato que deixou de existir. O endpoint
    // continua respondendo (e continua sob a política de rate limiting "listagem-paginada"),
    // mas devolve só OrientandosAtivos, que nunca foi paginado (List<T>, limitado por
    // Usuario.LimiteOrientandos). O escopo de docs/arquitetura/2026-07-12-paginacao-listagens.md
    // cai, portanto, de 4 para 3 endpoints paginados — não é perda silenciosa de cobertura, é
    // remoção do caminho de RBAC indevido (qualquer Professor via TODAS as propostas pendentes).
    //
    // O que sobrou de cobertura desse endpoint:
    //   - contrato pós-remoção + rotas de aprovar/rejeitar em 404:
    //     OrientadorNotificacaoIntegracao_Tests
    //   - listagem de pendentes (agora só do Coordenador, sem paginação):
    //     CoordenadorController_RejeitarProposta_Tests / DashboardTests (Client)
}
