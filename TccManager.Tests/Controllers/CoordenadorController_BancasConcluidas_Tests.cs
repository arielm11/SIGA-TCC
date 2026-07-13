using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração de GET /api/coordenador/bancas-concluidas (PagedResult&lt;BancaConcluidaDto&gt;).
/// Verifica: filtro por NotaFinal != null (bancas pendentes não aparecem), derivação de
/// Aprovado a partir de Tcc.Status == StatusTcc.Finalizado (e NÃO recalculado da nota),
/// ordenação por DataHora desc e paginação — reaproveitando os padrões de asserção usados em
/// Paginacao_Integracao_Tests.cs (Q3).
/// </summary>
public class CoordenadorController_BancasConcluidas_Tests
{
    private const int idCoordenador = 1;

    // Pequeno wrapper para semear via o contexto direto da factory sem repetir boilerplate.
    private sealed class AppDbContextSeeder
    {
        private readonly TccManager.Api.Data.AppDbContext _context;
        private int _seq;
        public AppDbContextSeeder(TccManager.Api.Data.AppDbContext context) => _context = context;

        public async Task<int> AddBancaAsync(decimal? notaFinal, StatusTcc statusTcc, DateTime dataHora, string titulo)
        {
            _seq++;
            var aluno = new Usuario { Nome = $"Aluno {_seq:D3}", Email = $"aluno{_seq}@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
            _context.Usuarios.Add(aluno);
            await _context.SaveChangesAsync();

            var tcc = new Tcc
            {
                Titulo = titulo,
                Resumo = "Resumo",
                AlunoId = aluno.Id,
                Status = statusTcc,
                DataCriacao = DateTime.UtcNow
            };
            _context.Tccs.Add(tcc);
            await _context.SaveChangesAsync();

            var banca = new Banca
            {
                TccId = tcc.Id,
                DataHora = dataHora,
                Local = "Sala",
                NotaFinal = notaFinal
            };
            _context.Banca.Add(banca);
            await _context.SaveChangesAsync();
            return banca.Id;
        }
    }

    [Fact]
    public async Task SoRetornaBancasComNotaFinal_NaoMisturaComPendentes()
    {
        var factory = new TccApiFactory();
        using var _ = factory;
        using (var context = factory.CriarContextoDireto())
        {
            var seeder = new AppDbContextSeeder(context);
            await seeder.AddBancaAsync(80.0m, StatusTcc.Finalizado, DateTime.UtcNow.AddDays(-2), "Concluida A");
            await seeder.AddBancaAsync(50.0m, StatusTcc.Reprovado, DateTime.UtcNow.AddDays(-1), "Concluida B");
            // Pendente: sem NotaFinal — não deve aparecer.
            await seeder.AddBancaAsync(null, StatusTcc.AguardandoDefesa, DateTime.UtcNow, "Pendente C");
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/bancas-concluidas");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<BancaConcluidaDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.TotalCount);
        Assert.All(pagina.Items, dto => Assert.DoesNotContain("Pendente", dto.TccTitulo));
    }

    [Fact]
    public async Task Aprovado_DerivaDoStatusFinalizado_NaoDaNota()
    {
        var factory = new TccApiFactory();
        using var _ = factory;
        int bancaNotaAltaReprovada, bancaNotaBaixaFinalizada;
        using (var context = factory.CriarContextoDireto())
        {
            var seeder = new AppDbContextSeeder(context);
            // Nota alta (>= qualquer limiar), mas Status Reprovado => Aprovado deve ser false.
            bancaNotaAltaReprovada = await seeder.AddBancaAsync(95.0m, StatusTcc.Reprovado, DateTime.UtcNow.AddDays(-1), "Alta mas reprovada");
            // Nota baixa, mas Status Finalizado => Aprovado deve ser true (segue o Status).
            bancaNotaBaixaFinalizada = await seeder.AddBancaAsync(10.0m, StatusTcc.Finalizado, DateTime.UtcNow.AddDays(-2), "Baixa mas finalizada");
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/bancas-concluidas?pageSize=100");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<BancaConcluidaDto>>();

        Assert.NotNull(pagina);
        var altaReprovada = pagina!.Items.Single(b => b.BancaId == bancaNotaAltaReprovada);
        var baixaFinalizada = pagina.Items.Single(b => b.BancaId == bancaNotaBaixaFinalizada);

        Assert.False(altaReprovada.Aprovado);
        Assert.True(baixaFinalizada.Aprovado);
    }

    [Fact]
    public async Task OrdenadasPorDataHoraDescendente()
    {
        var factory = new TccApiFactory();
        using var _ = factory;
        using (var context = factory.CriarContextoDireto())
        {
            var seeder = new AppDbContextSeeder(context);
            await seeder.AddBancaAsync(70.0m, StatusTcc.Finalizado, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Mais antiga");
            await seeder.AddBancaAsync(70.0m, StatusTcc.Finalizado, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), "Mais recente");
            await seeder.AddBancaAsync(70.0m, StatusTcc.Finalizado, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "Intermediaria");
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/bancas-concluidas");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<BancaConcluidaDto>>();

        Assert.NotNull(pagina);
        var datas = pagina!.Items.Select(i => i.DataHora).ToList();
        Assert.Equal(datas.OrderByDescending(d => d).ToList(), datas);
        Assert.Equal("Mais recente", pagina.Items.First().TccTitulo);
    }

    [Fact]
    public async Task Paginacao_TotalCountRefleteTotal_ComPaginaParcial()
    {
        var factory = new TccApiFactory();
        using var _ = factory;
        using (var context = factory.CriarContextoDireto())
        {
            var seeder = new AppDbContextSeeder(context);
            for (int i = 1; i <= 25; i++)
                await seeder.AddBancaAsync(70.0m, StatusTcc.Finalizado, DateTime.UtcNow.AddDays(-i), $"Banca {i:D3}");
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/bancas-concluidas?page=2&pageSize=10");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<BancaConcluidaDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(25, pagina!.TotalCount);
        Assert.Equal(3, pagina.TotalPages);
        Assert.Equal(2, pagina.CurrentPage);
        Assert.Equal(10, pagina.Items.Count);
    }

    [Fact]
    public async Task Paginacao_ValoresNaoNumericos_NaoRetornam400_UsamDefaults()
    {
        var factory = new TccApiFactory();
        using var _ = factory;
        using (var context = factory.CriarContextoDireto())
        {
            var seeder = new AppDbContextSeeder(context);
            await seeder.AddBancaAsync(70.0m, StatusTcc.Finalizado, DateTime.UtcNow, "Banca X");
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var response = await client.GetAsync("/api/coordenador/bancas-concluidas?page=abc&pageSize=xyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<BancaConcluidaDto>>();

        Assert.NotNull(pagina);
        Assert.Equal(PaginacaoQuery.DefaultPage, pagina!.CurrentPage);
        Assert.Equal(PaginacaoQuery.DefaultPageSize, pagina.PageSize);
        Assert.Equal(1, pagina.TotalCount);
    }
}
