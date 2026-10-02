using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #140 (achados M5/B4): PUT /api/usuario/{id} passa a exigir a senha atual em
/// autoedição quando Senha ou Email mudam, e revoga as sessões (refresh tokens) ativas do
/// usuário afetado sempre que a senha muda (qualquer caminho) ou um Admin altera Tipo/Ativo
/// de outro usuário.
/// </summary>
public class UsuarioController_RevogacaoDeSessao_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;

    private const string SenhaAtual = "senha-atual-valida-123";
    private static readonly string HashAluno = BCrypt.Net.BCrypt.HashPassword(SenhaAtual);

    private static async Task<TccApiFactory> CriarFactoryAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAdmin, Nome = "Admin Teste", Email = "admin@teste.com", SenhaHash = BCrypt.Net.BCrypt.HashPassword("outra-senha-admin"), Tipo = TipoUsuario.Admin, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOutroAluno, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true });

        await context.SaveChangesAsync();
        return factory;
    }

    private static async Task CriarRefreshTokenAtivoAsync(TccApiFactory factory, int usuarioId, string tokenHash)
    {
        using var context = factory.CriarContextoDireto();
        context.RefreshTokens.Add(new RefreshToken
        {
            UsuarioId = usuarioId,
            TokenHash = tokenHash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            RevokedAtUtc = null
        });
        await context.SaveChangesAsync();
    }

    private static async Task<bool> TemSessaoAtivaAsync(TccApiFactory factory, int usuarioId)
    {
        using var context = factory.CriarContextoDireto();
        return await context.RefreshTokens.AnyAsync(rt => rt.UsuarioId == usuarioId && rt.RevokedAtUtc == null);
    }

    // ───────────────────────── Senha atual exigida em autoedição ─────────────────────────

    [Fact]
    public async Task Autoedicao_TrocarSenha_SemInformarSenhaAtual_Retorna400ENaoAltera()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "nova-senha-valida-123", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var context = factory.CriarContextoDireto();
        var usuario = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(HashAluno, usuario!.SenhaHash);
    }

    [Fact]
    public async Task Autoedicao_TrocarSenha_ComSenhaAtualIncorreta_Retorna400ENaoAltera()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "nova-senha-valida-123", SenhaAtual = "senha-errada", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var context = factory.CriarContextoDireto();
        var usuario = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(HashAluno, usuario!.SenhaHash);
    }

    [Fact]
    public async Task Autoedicao_TrocarEmail_SemInformarSenhaAtual_Retorna400ENaoAltera()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "novo-email@teste.com", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var context = factory.CriarContextoDireto();
        var usuario = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("aluno@teste.com", usuario!.Email);
    }

    [Fact]
    public async Task Autoedicao_TrocarSenha_ComSenhaAtualCorreta_Permite()
    {
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "nova-senha-valida-123", SenhaAtual = SenhaAtual, Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        using var context = factory.CriarContextoDireto();
        var usuario = await context.Usuarios.FindAsync(IdAluno);
        Assert.True(BCrypt.Net.BCrypt.Verify("nova-senha-valida-123", usuario!.SenhaHash));
    }

    [Fact]
    public async Task Autoedicao_SemAlterarSenhaOuEmail_NaoExigeSenhaAtual()
    {
        // Alterar só o Nome não é considerado alteração sensível — não exige SenhaAtual.
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Nome Novo", Email = "aluno@teste.com", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Admin_RedefinindoSenhaDeOutroUsuario_NaoExigeSenhaAtualDoAlvo()
    {
        // Fluxo legítimo de redefinição pelo Admin: não é autoedição, SenhaAtual (do Aluno
        // alvo, que o Admin não conhece) não é exigida.
        using var factory = await CriarFactoryAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "senha-redefinida-pelo-admin-123", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
    }

    // ───────────────────────── Revogação de sessão ─────────────────────────

    [Fact]
    public async Task Autoedicao_TrocarSenha_RevogaSessoesAtivasDoProprioUsuario()
    {
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "nova-senha-valida-123", SenhaAtual = SenhaAtual, Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.False(await TemSessaoAtivaAsync(factory, IdAluno));
    }

    [Fact]
    public async Task Admin_RedefinindoSenhaDeOutroUsuario_RevogaSessoesAtivasDoAlvo()
    {
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "senha-redefinida-pelo-admin-123", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.False(await TemSessaoAtivaAsync(factory, IdAluno));
    }

    [Fact]
    public async Task Admin_AlterandoTipoDeOutroUsuario_RevogaSessoesAtivasDoAlvo()
    {
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Tipo = TipoUsuario.Professor, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.False(await TemSessaoAtivaAsync(factory, IdAluno));
    }

    [Fact]
    public async Task Admin_DesativandoOutroUsuario_RevogaSessoesAtivasDoAlvo()
    {
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Tipo = TipoUsuario.Aluno, Ativo = false };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.False(await TemSessaoAtivaAsync(factory, IdAluno));
    }

    [Fact]
    public async Task Admin_AlterandoApenasNome_NaoRevogaSessoesAtivas()
    {
        // Guarda de regressão: alterar só Nome (sem tocar em Senha/Tipo/Ativo/Email) não deve
        // disparar revogação — evita desconectar o usuário por uma edição não sensível.
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto { Nome = "Nome Atualizado", Email = "aluno@teste.com", Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.True(await TemSessaoAtivaAsync(factory, IdAluno));
    }

    [Fact]
    public async Task Autoedicao_TrocarSenha_NaoRevogaSessoesDeOutroUsuario()
    {
        using var factory = await CriarFactoryAsync();
        await CriarRefreshTokenAtivoAsync(factory, IdAluno, "hash-sessao-aluno");
        await CriarRefreshTokenAtivoAsync(factory, IdOutroAluno, "hash-sessao-outro-aluno");
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto { Nome = "Aluno Teste", Email = "aluno@teste.com", Senha = "nova-senha-valida-123", SenhaAtual = SenhaAtual, Tipo = TipoUsuario.Aluno, Ativo = true };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        response.EnsureSuccessStatusCode();
        Assert.True(await TemSessaoAtivaAsync(factory, IdOutroAluno));
    }
}
