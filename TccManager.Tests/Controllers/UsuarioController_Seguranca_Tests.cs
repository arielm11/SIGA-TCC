using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Cobre o lote de correcoes de seguranca do UsuarioController:
/// (1) GET /api/usuario/{id} nao vaza SenhaHash e exige Admin OU proprio id;
/// (2) POST /api/usuario deixou de ser [AllowAnonymous] e passou a exigir Admin;
/// (3) PUT /api/usuario/{id} exige Admin OU proprio id e ignora Tipo/Ativo em autoedicao
///     (bloqueio de escalacao de privilegio);
/// (4) nenhuma das respostas serializadas pode conter o hash da senha.
/// </summary>
public class UsuarioController_Seguranca_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;
    private const int IdProfessor = 20;
    private const int IdCoordenador = 30;
    private const int IdInexistente = 9999;

    private const string SenhaOriginal = "senha-original-123";

    // Hashes reais gerados uma unica vez por execucao: permitem tanto semear o banco
    // quanto assertar que o valor exato do hash nao aparece no payload de resposta.
    private static readonly string HashAdmin = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin");
    private static readonly string HashAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal);
    private static readonly string HashOutroAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-outro");
    private static readonly string HashProfessor = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-prof");
    private static readonly string HashCoordenador = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-coord");

    private static async Task<TccApiFactory> CriarFactoryComUsuariosAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAdmin, Nome = "Admin Teste", Email = "admin@teste.com", SenhaHash = HashAdmin, Tipo = TipoUsuario.Admin, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOutroAluno, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = HashOutroAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor Teste", Email = "professor@teste.com", SenhaHash = HashProfessor, Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = IdCoordenador, Nome = "Coordenador Teste", Email = "coord@teste.com", SenhaHash = HashCoordenador, Tipo = TipoUsuario.Coordenador, Ativo = true });

        await context.SaveChangesAsync();
        return factory;
    }

    /// <summary>
    /// Verifica o payload JSON bruto: nao basta o controller devolver UsuarioDto no tipo estatico,
    /// porque uma regressao futura poderia voltar a serializar a entidade Usuario sem erro de compilacao.
    /// </summary>
    private static void AssertPayloadSemVazamentoDeHash(string json, params string[] hashesSemeados)
    {
        Assert.DoesNotContain("SenhaHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$2a$", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$2b$", json, StringComparison.Ordinal);

        // Campos que so existem na entidade Usuario (nao no UsuarioDto): se aparecerem,
        // e sinal de que a entidade crua voltou a ser serializada.
        Assert.DoesNotContain("limiteOrientandos", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aceitandoOrientandos", json, StringComparison.OrdinalIgnoreCase);

        foreach (var hash in hashesSemeados)
        {
            Assert.DoesNotContain(hash, json, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/usuario/{id}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsuarioById_Admin_AcessandoOutroUsuario_Retorna200ComDtoSemSenhaHash()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.GetAsync($"/api/usuario/{IdAluno}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashAluno);

        var dto = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.NotNull(dto);
        Assert.Equal(IdAluno, dto!.Id);
        Assert.Equal("Aluno Teste", dto.Nome);
        Assert.Equal("aluno@teste.com", dto.Email);
        Assert.Equal(TipoUsuario.Aluno, dto.Tipo);
        Assert.True(dto.Ativo);
    }

    [Fact]
    public async Task GetUsuarioById_UsuarioComum_AcessandoProprioId_Retorna200SemSenhaHash()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync($"/api/usuario/{IdAluno}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashAluno);

        var dto = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(IdAluno, dto!.Id);
    }

    [Theory]
    [InlineData(IdAluno, "Aluno")]
    [InlineData(IdProfessor, "Professor")]
    [InlineData(IdCoordenador, "Coordenador")]
    public async Task GetUsuarioById_NaoAdmin_AcessandoIdDeOutro_Retorna403(int idChamador, string role)
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(idChamador, role);

        var response = await client.GetAsync($"/api/usuario/{IdOutroAluno}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashOutroAluno);
    }

    [Fact]
    public async Task GetUsuarioById_Anonimo_Retorna401()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClient(); // sem cabecalhos de autenticacao

        var response = await client.GetAsync($"/api/usuario/{IdAluno}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsuarioById_Admin_UsuarioInexistente_Retorna404()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.GetAsync($"/api/usuario/{IdInexistente}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsuarioById_NaoAdmin_IdInexistenteDeOutro_Retorna403NaoRevelandoExistencia()
    {
        // A checagem de autorizacao vem antes do FindAsync, entao o chamador recebe 403
        // (e nao 404) — impede enumeracao de ids validos por diferenca de status.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync($"/api/usuario/{IdInexistente}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/usuario
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateUsuario_Anonimo_Retorna401ENaoPersisteUsuario()
    {
        // Regressao do [AllowAnonymous] removido: antes qualquer um criava usuario sem autenticacao.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClient();

        var dto = new UsuarioDto
        {
            Nome = "Invasor",
            Email = "invasor@teste.com",
            Senha = "senha-invasor",
            Tipo = TipoUsuario.Admin,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.DoesNotContain(context.Usuarios, u => u.Email == "invasor@teste.com");
    }

    [Theory]
    [InlineData(IdAluno, "Aluno")]
    [InlineData(IdProfessor, "Professor")]
    [InlineData(IdCoordenador, "Coordenador")]
    public async Task CreateUsuario_AutenticadoNaoAdmin_Retorna403ENaoPersisteUsuario(int idChamador, string role)
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(idChamador, role);

        var dto = new UsuarioDto
        {
            Nome = "Novo Usuario",
            Email = "novo-nao-admin@teste.com",
            Senha = "senha-nova",
            Tipo = TipoUsuario.Professor,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.DoesNotContain(context.Usuarios, u => u.Email == "novo-nao-admin@teste.com");
    }

    [Fact]
    public async Task CreateUsuario_Admin_Retorna200ComDtoSemSenhaHashEPersisteHashNoBanco()
    {
        // Compatibilidade com TccManager.Client/Pages/Admin/FormUsuario.razor (POST api/usuario).
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Professor Novo",
            Email = "professor-novo@teste.com",
            Senha = "senha-do-novo-456",
            Tipo = TipoUsuario.Professor,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json);
        Assert.DoesNotContain("senha-do-novo-456", json, StringComparison.Ordinal);

        var criadoDto = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.NotNull(criadoDto);
        Assert.True(criadoDto!.Id > 0);
        Assert.Equal("Professor Novo", criadoDto.Nome);
        Assert.Equal(TipoUsuario.Professor, criadoDto.Tipo);
        Assert.True(criadoDto.Ativo);

        using var context = factory.CriarContextoDireto();
        var persistido = Assert.Single(context.Usuarios.Where(u => u.Email == "professor-novo@teste.com"));
        Assert.Equal(TipoUsuario.Professor, persistido.Tipo);
        Assert.NotEqual("senha-do-novo-456", persistido.SenhaHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("senha-do-novo-456", persistido.SenhaHash));
    }

    [Fact]
    public async Task CreateUsuario_Admin_EmailDuplicado_Retorna400()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Duplicado",
            Email = "aluno@teste.com",
            Senha = "qualquer",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT /api/usuario/{id}
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_Admin_EditandoOutroUsuario_AplicaTipoEAtivo()
    {
        // Compatibilidade com FormUsuario.razor: o Admin continua podendo alterar Tipo/Ativo.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Promovido",
            Email = "aluno-promovido@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Professor,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashAluno);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(TipoUsuario.Professor, retornado!.Tipo);
        Assert.False(retornado.Ativo);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("Aluno Promovido", persistido!.Nome);
        Assert.Equal("aluno-promovido@teste.com", persistido.Email);
        Assert.Equal(TipoUsuario.Professor, persistido.Tipo);
        Assert.False(persistido.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_AutoEdicao_TentandoVirarAdmin_TipoEIgnorado()
    {
        // Nucleo da correcao de escalacao de privilegio: um Aluno editando o proprio
        // registro nao pode se promover a Admin, mesmo enviando Tipo = Admin no corpo.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Editado",
            Email = "aluno-editado@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashAluno);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(TipoUsuario.Aluno, retornado!.Tipo);
        Assert.True(retornado.Ativo);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal(TipoUsuario.Aluno, persistido!.Tipo);   // continua Aluno no banco
        Assert.True(persistido.Ativo);                        // Ativo preservado pelo servidor
        Assert.Equal("Aluno Editado", persistido.Nome);       // campos nao sensiveis foram aplicados
        Assert.Equal("aluno-editado@teste.com", persistido.Email);
    }

    [Theory]
    [InlineData(IdProfessor, "Professor", TipoUsuario.Professor)]
    [InlineData(IdCoordenador, "Coordenador", TipoUsuario.Coordenador)]
    public async Task UpdateUsuario_AutoEdicao_NaoAdmin_NaoAlteraTipo(int idChamador, string role, TipoUsuario tipoOriginal)
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(idChamador, role);

        var dto = new UsuarioDto
        {
            Id = idChamador,
            Nome = "Nome Alterado",
            Email = $"alterado-{idChamador}@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{idChamador}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(idChamador);
        Assert.Equal(tipoOriginal, persistido!.Tipo);
        Assert.True(persistido.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_AutoEdicao_ComNovaSenha_AtualizaHashEMantemTipo()
    {
        // Compatibilidade com MeusDados.razor: o usuario logado continua trocando a propria senha.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            Senha = "nova-senha-789",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashAluno);
        Assert.DoesNotContain("nova-senha-789", json, StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.NotEqual(HashAluno, persistido!.SenhaHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("nova-senha-789", persistido.SenhaHash));
        Assert.Equal(TipoUsuario.Aluno, persistido.Tipo);
    }

    [Fact]
    public async Task UpdateUsuario_AutoEdicao_SenhaVazia_MantemHashAtual()
    {
        // MeusDados.razor envia Senha em branco quando o usuario nao quer trocar a senha.
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Sem Troca de Senha",
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
        Assert.True(BCrypt.Net.BCrypt.Verify(SenhaOriginal, persistido.SenhaHash));
    }

    [Theory]
    [InlineData(IdAluno, "Aluno")]
    [InlineData(IdProfessor, "Professor")]
    [InlineData(IdCoordenador, "Coordenador")]
    public async Task UpdateUsuario_NaoAdmin_EditandoOutroUsuario_Retorna403ENaoAltera(int idChamador, string role)
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(idChamador, role);

        var dto = new UsuarioDto
        {
            Id = IdOutroAluno,
            Nome = "Sequestrado",
            Email = "sequestrado@teste.com",
            Senha = "senha-do-atacante",
            Tipo = TipoUsuario.Admin,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdOutroAluno}", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        AssertPayloadSemVazamentoDeHash(json, HashOutroAluno);

        using var context = factory.CriarContextoDireto();
        var intacto = await context.Usuarios.FindAsync(IdOutroAluno);
        Assert.Equal("Outro Aluno", intacto!.Nome);
        Assert.Equal("outro@teste.com", intacto.Email);
        Assert.Equal(TipoUsuario.Aluno, intacto.Tipo);
        Assert.True(intacto.Ativo);
        Assert.Equal(HashOutroAluno, intacto.SenhaHash);
    }

    [Fact]
    public async Task UpdateUsuario_Anonimo_Retorna401ENaoAltera()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClient();

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Alterado Anonimamente",
            Email = "anonimo@teste.com",
            Senha = "senha",
            Tipo = TipoUsuario.Admin,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var intacto = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("Aluno Teste", intacto!.Nome);
        Assert.Equal(TipoUsuario.Aluno, intacto.Tipo);
    }

    [Fact]
    public async Task UpdateUsuario_Admin_UsuarioInexistente_Retorna404()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdInexistente,
            Nome = "Fantasma",
            Email = "fantasma@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdInexistente}", dto);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Nao vazamento do hash — verificacao dedicada no payload serializado
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayloadSerializado_GetCreateEUpdate_NuncaContemSenhaHash()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var clientAdmin = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var clientAluno = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var novo = new UsuarioDto
        {
            Nome = "Usuario Payload",
            Email = "payload@teste.com",
            Senha = "senha-payload-000",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var respostas = new List<(string rota, HttpResponseMessage resposta)>
        {
            ("GET /api/usuario/{id} (admin -> outro)", await clientAdmin.GetAsync($"/api/usuario/{IdAluno}")),
            ("GET /api/usuario/{id} (autoedicao)", await clientAluno.GetAsync($"/api/usuario/{IdAluno}")),
            ("GET /api/usuario/me", await clientAluno.GetAsync("/api/usuario/me")),
            ("GET /api/usuario (lista admin)", await clientAdmin.GetAsync("/api/usuario")),
            ("GET /api/usuario/professores", await clientAluno.GetAsync("/api/usuario/professores")),
            ("POST /api/usuario", await clientAdmin.PostAsJsonAsync("/api/usuario", novo)),
            ("PUT /api/usuario/{id} (admin)", await clientAdmin.PutAsJsonAsync($"/api/usuario/{IdProfessor}", new UsuarioDto
            {
                Id = IdProfessor,
                Nome = "Professor Renomeado",
                Email = "professor@teste.com",
                Senha = string.Empty,
                Tipo = TipoUsuario.Professor,
                Ativo = true
            })),
            ("PUT /api/usuario/{id} (autoedicao)", await clientAluno.PutAsJsonAsync($"/api/usuario/{IdAluno}", new UsuarioDto
            {
                Id = IdAluno,
                Nome = "Aluno Renomeado",
                Email = "aluno@teste.com",
                Senha = string.Empty,
                Tipo = TipoUsuario.Aluno,
                Ativo = true
            }))
        };

        foreach (var (rota, resposta) in respostas)
        {
            Assert.True(resposta.IsSuccessStatusCode, $"{rota} deveria responder com sucesso, veio {(int)resposta.StatusCode}.");

            var json = await resposta.Content.ReadAsStringAsync();

            Assert.DoesNotContain("SenhaHash", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(HashAdmin, json, StringComparison.Ordinal);
            Assert.DoesNotContain(HashAluno, json, StringComparison.Ordinal);
            Assert.DoesNotContain(HashOutroAluno, json, StringComparison.Ordinal);
            Assert.DoesNotContain(HashProfessor, json, StringComparison.Ordinal);
            Assert.DoesNotContain(HashCoordenador, json, StringComparison.Ordinal);
            Assert.DoesNotContain("$2a$", json, StringComparison.Ordinal);
            Assert.DoesNotContain("$2b$", json, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regressao dos endpoints nao alterados pelo lote
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsuarios_NaoAdmin_ContinuaRetornando403()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/usuario");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMeuPerfil_UsuarioComum_ContinuaRetornandoProprioDto()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/usuario/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(IdAluno, dto!.Id);
        Assert.Equal(TipoUsuario.Aluno, dto.Tipo);
    }

    [Fact]
    public async Task DeleteUsuario_NaoAdmin_ContinuaRetornando403()
    {
        using var factory = await CriarFactoryComUsuariosAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.DeleteAsync($"/api/usuario/{IdOutroAluno}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.NotNull(await context.Usuarios.FindAsync(IdOutroAluno));
    }
}
