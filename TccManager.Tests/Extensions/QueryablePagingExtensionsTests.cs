using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Api.Extensions;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Extensions;

public class QueryablePagingExtensionsTests
{
    private static AppDbContext CriarContexto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<AppDbContext> ContextoComProfessores(int quantidade)
    {
        var context = CriarContexto();
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
        return context;
    }

    [Fact]
    public async Task ValoresValidos_AplicamSkipTakeCorretamente()
    {
        using var context = await ContextoComProfessores(25);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 2, PageSize = 10 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(10, resultado.Items.Count);
        // Página 2 com 10 por página => itens 11..20 (Prof 011..Prof 020).
        Assert.Equal("Prof 011", resultado.Items.First().Nome);
        Assert.Equal("Prof 020", resultado.Items.Last().Nome);
    }

    [Fact]
    public async Task Metadados_RefletemTotalReal_MesmoComPoucosItensNaPagina()
    {
        using var context = await ContextoComProfessores(25);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 3, PageSize = 10 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        // Última página tem apenas 5 itens, mas o total permanece 25.
        Assert.Equal(5, resultado.Items.Count);
        Assert.Equal(25, resultado.TotalCount);
        Assert.Equal(3, resultado.TotalPages);
        Assert.Equal(3, resultado.CurrentPage);
        Assert.Equal(10, resultado.PageSize);
    }

    [Fact]
    public async Task PrimeiraPagina_ComecaDoInicioSemSkip()
    {
        using var context = await ContextoComProfessores(15);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 1, PageSize = 10 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(10, resultado.Items.Count);
        Assert.Equal("Prof 001", resultado.Items.First().Nome);
    }

    [Fact]
    public async Task PageSizeMaiorQueTotal_RetornaTodosOsItens()
    {
        using var context = await ContextoComProfessores(3);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 1, PageSize = PaginacaoQuery.MaxPageSize };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(3, resultado.Items.Count);
        Assert.Equal(3, resultado.TotalCount);
        Assert.Equal(1, resultado.TotalPages);
    }

    [Fact]
    public async Task PaginaAlemDoFim_ClampaParaUltimaPaginaReal()
    {
        // Regressão de segurança: Page muito além do fim não deve mais retornar vazio
        // com CurrentPage = valor pedido — isso permitia overflow de Int32 em
        // (Page-1)*PageSize para Page muito grande (ex.: ?page=21474837), gerando
        // Skip negativo e 500. Agora a página é clampada ao intervalo real.
        using var context = await ContextoComProfessores(5);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 10, PageSize = 10 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(5, resultado.Items.Count);
        Assert.Equal(5, resultado.TotalCount);
        Assert.Equal(1, resultado.TotalPages);
        Assert.Equal(1, resultado.CurrentPage);
    }

    [Fact]
    public async Task PaginaExtremamenteGrande_NaoLancaExcecaoPorOverflow()
    {
        using var context = await ContextoComProfessores(5);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = int.MaxValue, PageSize = 100 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(5, resultado.Items.Count);
        Assert.Equal(1, resultado.CurrentPage);
    }

    [Fact]
    public async Task QuerySemResultados_RetornaZerado()
    {
        using var context = CriarContexto();
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 1, PageSize = 20 };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Empty(resultado.Items);
        Assert.Equal(0, resultado.TotalCount);
        Assert.Equal(0, resultado.TotalPages);
    }

    [Theory]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(20, 10, 2)]
    [InlineData(21, 10, 3)]
    [InlineData(1, 20, 1)]
    public async Task TotalPages_UsaTetoDaDivisao(int totalItens, int pageSize, int totalPagesEsperado)
    {
        using var context = await ContextoComProfessores(totalItens);
        var query = context.Usuarios.OrderBy(u => u.Nome);
        var paginacao = new PaginacaoQuery { Page = 1, PageSize = pageSize };

        var resultado = await query.ToPagedResultAsync(paginacao);

        Assert.Equal(totalPagesEsperado, resultado.TotalPages);
    }
}
