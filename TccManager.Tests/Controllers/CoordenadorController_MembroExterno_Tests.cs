using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #69, item 5 — mass assignment em POST /api/coordenador/membros-externos.
///
/// O endpoint passou a receber <see cref="MembroExternoDto"/> em vez da entidade
/// <see cref="MembroExterno"/>. Como a assinatura mudou, o fluxo básico precisa de guarda de
/// regressão; e como o DTO ainda EXPÕE um campo Id (usado na leitura/GET), vale travar por
/// teste que esse Id vindo do corpo é ignorado na criação — o Id persistido é sempre o
/// gerado pelo banco.
/// </summary>
public class CoordenadorController_MembroExterno_Tests
{
    private const int IdCoordenador = 30;

    private static TccApiFactory CriarFactory()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = IdCoordenador,
            Nome = "Coordenador",
            Email = "coord@teste.com",
            SenhaHash = "x",
            Tipo = TipoUsuario.Coordenador,
            Ativo = true
        });
        context.SaveChanges();

        return factory;
    }

    [Fact]
    public async Task AdicionarMembroExterno_DtoValido_Retorna200EPersisteOsDados()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = new MembroExternoDto
        {
            Nome = "Maria Avaliadora",
            Email = "maria@universidade.edu.br",
            Instituicao = "Universidade Externa"
        };

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<MembroExterno>();
        Assert.NotNull(retornado);
        Assert.Equal("Maria Avaliadora", retornado!.Nome);
        Assert.Equal("maria@universidade.edu.br", retornado.Email);
        Assert.Equal("Universidade Externa", retornado.Instituicao);
        Assert.True(retornado.Id > 0, "O Id devolvido deve ser o gerado pela persistência.");

        using var context = factory.CriarContextoDireto();
        var persistido = await context.MembrosExternos.SingleAsync();
        Assert.Equal(retornado.Id, persistido.Id);
        Assert.Equal("Maria Avaliadora", persistido.Nome);
        Assert.Equal("maria@universidade.edu.br", persistido.Email);
        Assert.Equal("Universidade Externa", persistido.Instituicao);
    }

    [Fact]
    public async Task AdicionarMembroExterno_ComIdNoCorpo_IgnoraOIdEnviadoPeloCliente()
    {
        // Núcleo do item 5: mesmo que o cliente mande um Id (o DTO tem a propriedade), a
        // entidade é construída no controller sem Id — nada do corpo alcança a chave primária.
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = new MembroExternoDto
        {
            Id = 987654,
            Nome = "Joao Externo",
            Email = "joao@instituto.org",
            Instituicao = "Instituto Parceiro"
        };

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<MembroExterno>();
        Assert.NotEqual(987654, retornado!.Id);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.MembrosExternos.AnyAsync(m => m.Id == 987654));
        Assert.Equal(retornado.Id, (await context.MembrosExternos.SingleAsync()).Id);
    }

    [Fact]
    public async Task AdicionarMembroExterno_UsuarioSemPapelCoordenador_Retorna403()
    {
        // O controller inteiro é [Authorize(Roles = "Coordenador")]; a troca do parâmetro
        // para DTO não pode ter afetado a autorização.
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(10, "Aluno");

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto
        {
            Nome = "Tentativa",
            Email = "tentativa@teste.com",
            Instituicao = "X"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.MembrosExternos.AnyAsync());
    }

    [Fact]
    public async Task AdicionarMembroExterno_DepoisAparecerNaListagem()
    {
        // Fecha o ciclo com o GET, que já projeta para MembroExternoDto: o membro criado
        // pelo novo contrato continua legível pelo contrato de leitura existente.
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var criado = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto
        {
            Nome = "Ana Externa",
            Email = "ana@externa.edu",
            Instituicao = "Faculdade Externa"
        });
        Assert.Equal(HttpStatusCode.OK, criado.StatusCode);

        var listagem = await client.GetAsync("/api/coordenador/membros-externos");
        Assert.Equal(HttpStatusCode.OK, listagem.StatusCode);

        var corpo = await listagem.Content.ReadAsStringAsync();
        Assert.Contains("Ana Externa", corpo, StringComparison.Ordinal);
        Assert.Contains("ana@externa.edu", corpo, StringComparison.Ordinal);
    }
}
