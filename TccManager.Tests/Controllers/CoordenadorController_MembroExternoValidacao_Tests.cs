using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #70, item 4 — <c>MembroExternoDtoValidator</c> aplicado de ponta a ponta.
///
/// O validator entra em vigor pelo <c>FluentValidationActionFilter</c> registrado
/// globalmente (mesmo mecanismo do <c>UsuarioDtoValidator</c>), então a garantia que
/// interessa é comportamental: POST e PUT de membro externo devolvem 400 e **não**
/// tocam no banco quando o DTO é inválido. Sem isso, valores vazios chegariam a colunas
/// NOT NULL e e-mails inválidos/gigantes virariam destinatário de notificação.
///
/// Os casos de caminho feliz do POST (persistência, Id ignorado, autorização, listagem)
/// já estão em <c>CoordenadorController_MembroExterno_Tests</c> e não são repetidos aqui;
/// só o caminho feliz do PUT é coberto, por ser o contraponto necessário aos 400.
/// </summary>
public class CoordenadorController_MembroExternoValidacao_Tests
{
    private const int IdCoordenador = 30;
    private const int IdMembroExistente = 5;

    private const string NomeOriginal = "Membro Original";
    private const string EmailOriginal = "original@instituto.org";
    private const string InstituicaoOriginal = "Instituto Original";

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

        context.MembrosExternos.Add(new MembroExterno
        {
            Id = IdMembroExistente,
            Nome = NomeOriginal,
            Email = EmailOriginal,
            Instituicao = InstituicaoOriginal
        });

        context.SaveChanges();
        return factory;
    }

    private static MembroExternoDto DtoValido() => new()
    {
        Nome = "Carlos Externo",
        Email = "carlos@faculdade.edu.br",
        Instituicao = "Faculdade Parceira"
    };

    /// <summary>Confere que o membro pré-existente permaneceu exatamente como estava.</summary>
    private static async Task AssertMembroInalteradoAsync(TccApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();
        var membro = await context.MembrosExternos.SingleAsync(m => m.Id == IdMembroExistente);

        Assert.Equal(NomeOriginal, membro.Nome);
        Assert.Equal(EmailOriginal, membro.Email);
        Assert.Equal(InstituicaoOriginal, membro.Instituicao);
        // E nenhum membro novo foi criado pelo POST rejeitado.
        Assert.Equal(1, await context.MembrosExternos.CountAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/coordenador/membros-externos
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdicionarMembroExterno_NomeVazio_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Nome = string.Empty;

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_NomeApenasEspacos_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Nome = "   ";

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_EmailVazio_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = string.Empty;

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_EmailComFormatoInvalido_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = "nao-e-um-email";

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_EmailComMaisDe450Caracteres_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = new string('a', 445) + "@teste.com"; // 455 caracteres

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_InstituicaoVazia_Retorna400ENaoPersiste()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Instituicao = string.Empty;

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_TodosOsCamposInvalidos_Retorna400ComOsTresErros()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // ValidationProblemDetails: o corpo agrega os erros por propriedade.
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(MembroExternoDto.Nome), corpo, StringComparison.Ordinal);
        Assert.Contains(nameof(MembroExternoDto.Email), corpo, StringComparison.Ordinal);
        Assert.Contains(nameof(MembroExternoDto.Instituicao), corpo, StringComparison.Ordinal);

        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AdicionarMembroExterno_EmailComExatamente450Caracteres_Retorna200()
    {
        // Guarda do limite exato (nvarchar(450) na coluna): reduzir o MaximumLength
        // por engano precisa quebrar a suíte.
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var emailNoLimite = new string('a', 440) + "@teste.com"; // 450 caracteres
        var dto = DtoValido();
        dto.Email = emailNoLimite;

        var response = await client.PostAsJsonAsync("/api/coordenador/membros-externos", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.True(await context.MembrosExternos.AnyAsync(m => m.Email == emailNoLimite));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT /api/coordenador/membros-externos/{id}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AtualizarMembroExterno_DtoValido_Retorna200EPersisteAsAlteracoes()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var membro = await context.MembrosExternos.SingleAsync(m => m.Id == IdMembroExistente);
        Assert.Equal(dto.Nome, membro.Nome);
        Assert.Equal(dto.Email, membro.Email);
        Assert.Equal(dto.Instituicao, membro.Instituicao);
    }

    [Fact]
    public async Task AtualizarMembroExterno_NomeVazio_Retorna400ENaoAlteraNada()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Nome = string.Empty;

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AtualizarMembroExterno_EmailVazio_Retorna400ENaoAlteraNada()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = string.Empty;

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AtualizarMembroExterno_EmailComFormatoInvalido_Retorna400ENaoAlteraNada()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = "nao-e-um-email";

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AtualizarMembroExterno_EmailComMaisDe450Caracteres_Retorna400ENaoAlteraNada()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = new string('b', 445) + "@teste.com"; // 455 caracteres

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AtualizarMembroExterno_InstituicaoVazia_Retorna400ENaoAlteraNada()
    {
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Instituicao = "   ";

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{IdMembroExistente}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }

    [Fact]
    public async Task AtualizarMembroExterno_DtoInvalido_ValidacaoOcorreAntesDaBuscaPorId()
    {
        // Membro inexistente + DTO inválido: o filtro global roda antes da action, então
        // a resposta é 400 (validação), não 404. Trava a ordem de avaliação.
        using var factory = CriarFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var dto = DtoValido();
        dto.Email = "nao-e-um-email";

        var response = await client.PutAsJsonAsync("/api/coordenador/membros-externos/999", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertMembroInalteradoAsync(factory);
    }
}
