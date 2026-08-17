using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #61 — auditoria de segurança (logs de falha de login, de acesso negado no
/// UsuarioController e de alteração de campos sensíveis por Admin).
///
/// Escolha deliberada de escopo: NÃO se testa o texto das mensagens de log. Message template
/// é frágil e refatorá-lo não é regressão. O que precisa ficar travado por teste é que a
/// auditoria é um EFEITO COLATERAL — o contrato HTTP (status e corpo) não pode ter mudado, e
/// nenhum dos ramos novos pode lançar (todos leem <c>HttpContext.Connection.RemoteIpAddress</c>
/// e claims que podem ser nulos).
///
/// Verificar o conteúdo do log em si (inclusive a garantia de LGPD de que o e-mail tentado
/// nunca aparece) fica registrado como pendência para o qa-agent — ver relatório.
/// </summary>
public class Auth_Auditoria_Contrato_Tests
{
    private const string RotaLogin = "/api/auth/login";
    private const string SenhaValida = "SenhaValida123";

    private const int IdAdmin = 1;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;

    private static async Task SemearAsync(TccApiFactory factory, bool alunoAtivo = true)
    {
        using var context = factory.CriarContextoDireto();
        context.Usuarios.AddRange(
            new Usuario
            {
                Id = IdAdmin,
                Nome = "Admin",
                Email = "admin@teste.com",
                SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
                Tipo = TipoUsuario.Admin,
                Ativo = true
            },
            new Usuario
            {
                Id = IdAluno,
                Nome = "Aluno",
                Email = "aluno@teste.com",
                SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
                Tipo = TipoUsuario.Aluno,
                Ativo = alunoAtivo
            },
            new Usuario
            {
                Id = IdOutroAluno,
                Nome = "Outro Aluno",
                Email = "outro@teste.com",
                SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
                Tipo = TipoUsuario.Aluno,
                Ativo = true
            });
        await context.SaveChangesAsync();
    }

    // ─────────── Login: os 3 ramos de falha auditados mantêm o contrato ───────────

    [Fact]
    public async Task Login_UsuarioNaoEncontrado_Continua401ComMensagemEspecifica()
    {
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "ninguem@teste.com", Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Usuário não encontrado.", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_SenhaIncorreta_Continua401ComMensagemEspecifica()
    {
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "aluno@teste.com", Senha = "senha-errada" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Senha incorreta.", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_UsuarioInativo_Continua401ComMensagemEspecifica()
    {
        var factory = new TccApiFactory();
        await SemearAsync(factory, alunoAtivo: false);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "aluno@teste.com", Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("Usuário inativo.", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_CredenciaisValidas_Continua200()
    {
        // A instrumentação não pode ter afetado o caminho feliz.
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(RotaLogin,
            new LoginDto { Email = "aluno@teste.com", Senha = SenhaValida });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.False(string.IsNullOrWhiteSpace(dto!.Token));
        Assert.False(string.IsNullOrWhiteSpace(dto.RefreshToken));
    }

    // ─────────── UsuarioController: ramos auditados mantêm o contrato ───────────

    [Fact]
    public async Task GetUsuarioById_AcessoNegado_Continua403()
    {
        // Ramo que passou a logar: o Forbid() precisa continuar sendo Forbid(), e a leitura
        // do claim de id (que pode ser nula) não pode lançar.
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resp = await client.GetAsync($"/api/usuario/{IdOutroAluno}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateUsuario_AcessoNegado_Continua403ENaoAltera()
    {
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resp = await client.PutAsJsonAsync($"/api/usuario/{IdOutroAluno}", new UsuarioDto
        {
            Id = IdOutroAluno,
            Nome = "Sequestrado",
            Email = "sequestrado@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        using var context = factory.CriarContextoDireto();
        var intacto = await context.Usuarios.FindAsync(IdOutroAluno);
        Assert.Equal(TipoUsuario.Aluno, intacto!.Tipo);
        Assert.True(intacto.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_AdminAlteraTipoEAtivo_Continua200EPersisteOsNovosValores()
    {
        // Este é o ramo que dispara o log de "alteração de campos sensíveis". O log é
        // colateral: a alteração em si tem que continuar sendo aplicada normalmente.
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var resp = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno",
            Email = "aluno@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Professor,
            Ativo = false
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(TipoUsuario.Professor, persistido!.Tipo);
        Assert.False(persistido.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_AdminSemAlterarTipoNemAtivo_Continua200()
    {
        // Ramo em que a comparação "antigo != novo" é falsa e NENHUM log é emitido.
        // Cobre a aresta oposta da condicional introduzida pela auditoria.
        var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var resp = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Renomeado",
            Email = "aluno@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("Aluno Renomeado", persistido!.Nome);
        Assert.Equal(TipoUsuario.Aluno, persistido.Tipo);
        Assert.True(persistido.Ativo);
    }
}
