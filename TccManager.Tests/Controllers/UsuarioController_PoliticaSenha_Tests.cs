using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Cobre a issue #90 (achado A07-1): POST/PUT /api/usuario passaram a exigir a mesma
/// <c>PoliticaSenha</c> já usada pelo bootstrap de Admin e pela troca de senha obrigatória
/// (issue #88) — antes desta correção, POST sem o campo Senha criava um usuário com senha
/// vazia funcional (BCrypt.Verify("", hash) == true).
/// </summary>
public class UsuarioController_PoliticaSenha_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;

    private const string SenhaOriginal = "senha-original-123";

    private static readonly string HashAdmin = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin");
    private static readonly string HashAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal);

    private static async Task<TccApiFactory> CriarFactoryAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAdmin, Nome = "Admin Teste", Email = "admin@teste.com", SenhaHash = HashAdmin, Tipo = TipoUsuario.Admin, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true });

        await context.SaveChangesAsync();
        return factory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/usuario
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateUsuario_Admin_SenhaVazia_Retorna400ENaoCriaUsuario()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Usuario Sem Senha",
            Email = "sem-senha@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Usuarios.AnyAsync(u => u.Email == "sem-senha@teste.com"));
    }

    [Fact]
    public async Task CreateUsuario_Admin_SenhaMenorQue12Caracteres_Retorna400ENaoCriaUsuario()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Usuario Senha Curta",
            Email = "senha-curta@teste.com",
            Senha = "curta12345", // 10 caracteres
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("mínimo", corpo, StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Usuarios.AnyAsync(u => u.Email == "senha-curta@teste.com"));
    }

    [Fact]
    public async Task CreateUsuario_Admin_SenhaIgualAoEmail_Retorna400ENaoCriaUsuario()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        const string email = "senha-igual-email@teste.com";

        var dto = new UsuarioDto
        {
            Nome = "Usuario Senha Igual Email",
            Email = email,
            Senha = email, // mais de 12 caracteres, mas igual ao proprio email
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Usuarios.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task CreateUsuario_Admin_SenhaMaiorQue72BytesUtf8_Retorna400ENaoCriaUsuario()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Usuario Senha Longa",
            Email = "senha-longa@teste.com",
            Senha = new string('a', 73), // 73 bytes UTF-8 (limite do BCrypt e' 72)
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Usuarios.AnyAsync(u => u.Email == "senha-longa@teste.com"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT /api/usuario/{id}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_Admin_TrocandoSenhaPorUmaMenorQue12Caracteres_Retorna400ENaoAlteraSenha()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            Senha = "curta", // 5 caracteres
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(HashAluno, persistido!.SenhaHash); // senha antiga preservada
    }

    [Fact]
    public async Task UpdateUsuario_Admin_TrocandoSenhaIgualAoEmailDoAlvo_Retorna400ENaoAlteraSenha()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            Senha = "aluno@teste.com", // igual ao proprio email do alvo
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(HashAluno, persistido!.SenhaHash);
    }

    [Fact]
    public async Task UpdateUsuario_AutoEdicao_SenhaVazia_NaoAcionaPoliticaEMantemSenhaAtual()
    {
        // Senha vazia continua significando "nao trocar" — a politica so vale quando
        // uma nova senha e de fato enviada.
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Renomeado",
            Email = "aluno@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(HashAluno, persistido!.SenhaHash);
        Assert.Equal("Aluno Renomeado", persistido.Nome);
    }
}
