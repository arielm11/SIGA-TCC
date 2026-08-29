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
/// Issue #75 — <c>GET /api/tcc/entregas</c> (<see cref="TccManager.Api.Controllers.TccController.GetMinhasEntregas"/>)
/// nunca tinha cobertura própria: os únicos testes que batiam nessa rota eram o smoke test de
/// rate limiting (só checava 429/200, nunca o corpo) e os testes de upload (rota POST). Aqui:
/// conteúdo retornado, ordenação, paginação, lista vazia, TCC inexistente e autorização.
/// </summary>
public class TccController_GetMinhasEntregas_Tests
{
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private static async Task<int> SemearTccComEntregasAsync(WebRootIsolatedApiFactory factory, int quantidadeEntregas)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            SenhaHash = "x",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        for (var i = 0; i < quantidadeEntregas; i++)
        {
            context.Entregas.Add(new Entrega
            {
                TccId = tcc.Id,
                Titulo = $"Entrega {i:D2}",
                ArquivoCaminho = $"/uploads/entregas/fake-{i}.pdf",
                Tipo = TipoEntrega.Parcial,
                DataEnvio = DateTime.UtcNow.AddDays(-quantidadeEntregas + i) // i=0 é a mais antiga
            });
        }
        await context.SaveChangesAsync();

        return tcc.Id;
    }

    [Fact]
    public async Task ComEntregas_RetornaConteudoOrdenadoPorDataEnvioDescendente()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccComEntregasAsync(factory, quantidadeEntregas: 3);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resultado = await client.GetFromJsonAsync<PagedResult<Entrega>>("/api/tcc/entregas");

        Assert.NotNull(resultado);
        Assert.Equal(3, resultado!.TotalCount);
        Assert.Equal(3, resultado.Items.Count);
        // A mais recente (i=2, "Entrega 02") deve vir primeiro.
        Assert.Equal("Entrega 02", resultado.Items[0].Titulo);
        Assert.Equal("Entrega 00", resultado.Items[2].Titulo);
    }

    [Fact]
    public async Task ComMaisEntregasQueOPageSize_RespeitaPaginacao()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccComEntregasAsync(factory, quantidadeEntregas: 5);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var pagina1 = await client.GetFromJsonAsync<PagedResult<Entrega>>("/api/tcc/entregas?page=1&pageSize=2");
        var pagina2 = await client.GetFromJsonAsync<PagedResult<Entrega>>("/api/tcc/entregas?page=2&pageSize=2");

        Assert.NotNull(pagina1);
        Assert.NotNull(pagina2);
        Assert.Equal(5, pagina1!.TotalCount);
        Assert.Equal(2, pagina1.Items.Count);
        Assert.Equal(2, pagina2!.Items.Count);
        Assert.Equal(3, pagina1.TotalPages);
        // Páginas não podem se sobrepor.
        Assert.DoesNotContain(pagina2.Items, e2 => pagina1.Items.Any(e1 => e1.Id == e2.Id));
    }

    [Fact]
    public async Task TccSemNenhumaEntrega_RetornaListaVazia()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccComEntregasAsync(factory, quantidadeEntregas: 0);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resultado = await client.GetFromJsonAsync<PagedResult<Entrega>>("/api/tcc/entregas");

        Assert.NotNull(resultado);
        Assert.Empty(resultado!.Items);
        Assert.Equal(0, resultado.TotalCount);
    }

    [Fact]
    public async Task AlunoSemTcc_RetornaNotFound()
    {
        using var factory = new WebRootIsolatedApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario
            {
                Id = IdAluno, Nome = "Aluno Sem TCC", Email = "semtcc@teste.com",
                SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true
            });
            await context.SaveChangesAsync();
        }
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/entregas");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TccReprovado_NaoConta_RetornaNotFound()
    {
        // GetMinhasEntregas filtra Status != Reprovado — mesma regra usada em EnviarEntrega.
        using var factory = new WebRootIsolatedApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario
            {
                Id = IdAluno, Nome = "Aluno Reprovado", Email = "reprovado@teste.com",
                SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true
            });
            context.Tccs.Add(new Tcc
            {
                Titulo = "TCC Reprovado", Resumo = "r", AlunoId = IdAluno,
                Status = StatusTcc.Reprovado, DataCriacao = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/tcc/entregas");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SemAutenticacao_RetornaUnauthorized()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tcc/entregas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Professor")]
    [InlineData("Coordenador")]
    public async Task PapelDiferenteDeAluno_RetornaForbidden(string papel)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdProfessor, papel);

        var response = await client.GetAsync("/api/tcc/entregas");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
