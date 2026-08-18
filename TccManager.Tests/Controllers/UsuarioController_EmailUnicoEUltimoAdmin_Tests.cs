using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Cobre o lote de correcoes #65 e #67 do UsuarioController:
/// (#65) PUT /api/usuario/{id} passou a rejeitar e-mail ja usado por OUTRO usuario
///       ("Email já cadastrado", 400), com reforco de indice unico em usuarios.Email;
/// (#67) o sistema nao pode ficar sem nenhum Admin ativo — DELETE recusa excluir o
///       ultimo Admin ativo (400) e PUT bloqueia silenciosamente a mudanca de
///       Tipo/Ativo do ultimo Admin ativo (200, campos sensiveis preservados).
/// </summary>
public class UsuarioController_EmailUnicoEUltimoAdmin_Tests
{
    private const int IdAdmin = 1;
    private const int IdSegundoAdmin = 2;
    private const int IdAdminInativo = 3;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;
    private const int IdProfessor = 20;

    private const string SenhaOriginal = "senha-original-123";

    private static readonly string HashAdmin = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin");
    private static readonly string HashSegundoAdmin = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin2");
    private static readonly string HashAdminInativo = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin-inativo");
    private static readonly string HashAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal);
    private static readonly string HashOutroAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-outro");
    private static readonly string HashProfessor = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-prof");

    private static Usuario[] UsuariosBase() =>
    [
        new Usuario { Id = IdAdmin, Nome = "Admin Teste", Email = "admin@teste.com", SenhaHash = HashAdmin, Tipo = TipoUsuario.Admin, Ativo = true },
        new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
        new Usuario { Id = IdOutroAluno, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = HashOutroAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
        new Usuario { Id = IdProfessor, Nome = "Professor Teste", Email = "professor@teste.com", SenhaHash = HashProfessor, Tipo = TipoUsuario.Professor, Ativo = true }
    ];

    private static async Task<TccApiFactory> CriarFactoryAsync(params Usuario[] usuariosExtras)
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(UsuariosBase());

        if (usuariosExtras.Length > 0)
            context.Usuarios.AddRange(usuariosExtras);

        await context.SaveChangesAsync();
        return factory;
    }

    /// <summary>Cenario padrao: IdAdmin e o unico Admin ativo do sistema.</summary>
    private static Task<TccApiFactory> CriarFactoryComUnicoAdminAtivoAsync() => CriarFactoryAsync();

    /// <summary>Cenario com 2 Admins ativos: a protecao do "ultimo Admin" nao deve disparar.</summary>
    private static Task<TccApiFactory> CriarFactoryComDoisAdminsAtivosAsync() =>
        CriarFactoryAsync(new Usuario
        {
            Id = IdSegundoAdmin,
            Nome = "Segundo Admin",
            Email = "admin2@teste.com",
            SenhaHash = HashSegundoAdmin,
            Tipo = TipoUsuario.Admin,
            Ativo = true
        });

    // ─────────────────────────────────────────────────────────────────────────
    // #65 — PUT /api/usuario/{id}: e-mail duplicado
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_Admin_EmailJaUsadoPorTerceiro_Retorna400ENaoAlteraNadaDoAlvo()
    {
        // Nucleo da #65: antes da correcao a edicao gravava o e-mail duplicado.
        // A rejeicao precisa ser atomica — nenhum campo do alvo pode ser persistido.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Nome Que Nao Deve Ser Salvo",
            Email = "outro@teste.com", // ja pertence a IdOutroAluno
            Senha = "senha-que-nao-deve-ser-salva",
            Tipo = TipoUsuario.Professor,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("Email já cadastrado", corpo, StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();

        var alvo = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("Aluno Teste", alvo!.Nome);
        Assert.Equal("aluno@teste.com", alvo.Email);
        Assert.Equal(TipoUsuario.Aluno, alvo.Tipo);
        Assert.True(alvo.Ativo);
        Assert.Equal(HashAluno, alvo.SenhaHash);

        // O dono legitimo do e-mail tambem permanece intacto.
        var terceiro = await context.Usuarios.FindAsync(IdOutroAluno);
        Assert.Equal("Outro Aluno", terceiro!.Nome);
        Assert.Equal("outro@teste.com", terceiro.Email);

        Assert.Single(context.Usuarios.Where(u => u.Email == "outro@teste.com"));
    }

    [Fact]
    public async Task UpdateUsuario_AutoEdicao_MantendoProprioEmail_Retorna200SemFalsoPositivo()
    {
        // O proprio e-mail do usuario nao pode ser tratado como "ja cadastrado".
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Com Nome Novo",
            Email = "aluno@teste.com", // inalterado
            Senha = "nova-senha-do-aluno-987",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal("Aluno Com Nome Novo", retornado!.Nome);
        Assert.Equal("aluno@teste.com", retornado.Email);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("Aluno Com Nome Novo", persistido!.Nome);
        Assert.Equal("aluno@teste.com", persistido.Email);
        Assert.NotEqual(HashAluno, persistido.SenhaHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("nova-senha-do-aluno-987", persistido.SenhaHash));
    }

    [Fact]
    public async Task UpdateUsuario_Admin_AutoEdicaoMantendoProprioEmail_Retorna200SemFalsoPositivo()
    {
        // Mesmo caso acima pela via Admin (o Admin cai no ramo de campos sensiveis).
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Renomeado",
            Email = "admin@teste.com", // inalterado
            Senha = "nova-senha-do-admin-654",
            Tipo = TipoUsuario.Admin,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal("Admin Renomeado", persistido!.Nome);
        Assert.Equal("admin@teste.com", persistido.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify("nova-senha-do-admin-654", persistido.SenhaHash));
    }

    [Fact]
    public async Task UpdateUsuario_Admin_EmailInexistenteNoSistema_Retorna200EPersisteNovoEmail()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Com E-mail Novo",
            Email = "aluno-email-novo@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal("aluno-email-novo@teste.com", retornado!.Email);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAluno);
        Assert.Equal("aluno-email-novo@teste.com", persistido!.Email);
        Assert.Equal("Aluno Com E-mail Novo", persistido.Nome);
        Assert.Equal(HashAluno, persistido.SenhaHash); // Senha vazia nao troca o hash
        Assert.DoesNotContain(context.Usuarios, u => u.Email == "aluno@teste.com");
    }

    [Fact]
    public async Task UpdateUsuario_Admin_TrocandoEmailEntreDoisUsuariosEmSequencia_SegundaTrocaEhRejeitada()
    {
        // Regressao de borda: apos liberar um e-mail, ele pode ser reusado; mas o
        // e-mail recem-ocupado passa a bloquear a proxima tentativa.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var liberando = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "aluno-liberou@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        Assert.Equal(HttpStatusCode.OK, liberando.StatusCode);

        var reusando = await client.PutAsJsonAsync($"/api/usuario/{IdOutroAluno}", new UsuarioDto
        {
            Id = IdOutroAluno,
            Nome = "Outro Aluno",
            Email = "aluno@teste.com", // e-mail que acabou de ficar livre
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        Assert.Equal(HttpStatusCode.OK, reusando.StatusCode);

        var colidindo = await client.PutAsJsonAsync($"/api/usuario/{IdProfessor}", new UsuarioDto
        {
            Id = IdProfessor,
            Nome = "Professor Teste",
            Email = "aluno@teste.com", // agora pertence a IdOutroAluno
            Senha = string.Empty,
            Tipo = TipoUsuario.Professor,
            Ativo = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, colidindo.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Single(context.Usuarios.Where(u => u.Email == "aluno@teste.com"));
        Assert.Equal("professor@teste.com", (await context.Usuarios.FindAsync(IdProfessor))!.Email);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // #65 — camada de banco: indice unico em usuarios.Email
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModeloEfCore_Usuario_DeclaraIndiceUnicoNoEmail()
    {
        // Guarda de regressao da configuracao em AppDbContext.OnModelCreating:
        // se alguem remover o HasIndex(...).IsUnique(), a migration futura deixaria
        // de reforcar a invariante no banco.
        using var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var entidade = context.Model.FindEntityType(typeof(Usuario));
        Assert.NotNull(entidade);

        var indiceEmail = entidade!.GetIndexes()
            .SingleOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Usuario.Email));

        Assert.NotNull(indiceEmail);
        Assert.True(indiceEmail!.IsUnique, "O indice de usuarios.Email precisa ser UNIQUE.");
    }

    [Fact]
    public void DdlSqlServer_ContemIndiceUnicoEmUsuariosEmail()
    {
        // O provider InMemory usado pela suite NAO aplica indices unicos, entao a
        // insercao direta de duplicados nao lanca DbUpdateException nesse harness
        // (ver teste InsercaoDireta_... abaixo). Aqui o schema relacional real e
        // conferido offline: gera-se o DDL do provider SQL Server (sem conexao) e
        // verifica-se que o indice unico de usuarios.Email esta presente.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=servidor-inexistente;Database=TccManagerDdl;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new AppDbContext(options);
        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE UNIQUE INDEX [IX_usuarios_Email] ON [usuarios] ([Email])", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsercaoDiretaDeEmailDuplicado_ProviderInMemory_NaoAplicaIndiceUnico_LimitacaoConhecida()
    {
        // Documenta explicitamente a limitacao do harness: o EF Core InMemory ignora
        // indices unicos, portanto a "segunda linha de defesa" (constraint do banco)
        // NAO e exercitavel aqui — quem protege nesta suite e a validacao de aplicacao
        // (CreateUsuario/UpdateUsuario). Se a suite migrar para um provider relacional,
        // este teste falha de proposito e deve ser convertido em
        // Assert.Throws<DbUpdateException>. Verificacao em banco real: pendencia de QA.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Nome = "Clone do Aluno",
            Email = "aluno@teste.com", // ja existe
            SenhaHash = HashAluno,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });

        var excecao = await Record.ExceptionAsync(() => context.SaveChangesAsync());

        Assert.Null(excecao);
        Assert.Equal(2, context.Usuarios.Count(u => u.Email == "aluno@teste.com"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // #67 — DELETE /api/usuario/{id}: protecao do ultimo Admin ativo
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteUsuario_UnicoAdminAtivo_Retorna400ENaoRemoveDoBanco()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.DeleteAsync($"/api/usuario/{IdAdmin}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("Não é possível excluir o único Admin ativo do sistema.", corpo, StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.NotNull(persistido);
        Assert.Equal(TipoUsuario.Admin, persistido!.Tipo);
        Assert.True(persistido.Ativo);
        Assert.Equal(1, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    [Fact]
    public async Task DeleteUsuario_UnicoAdminAtivoComOutroAdminInativo_Retorna400()
    {
        // Admin inativo nao conta como "Admin disponivel": excluir o unico ativo
        // deixaria o sistema sem ninguem capaz de administrar.
        using var factory = await CriarFactoryAsync(new Usuario
        {
            Id = IdAdminInativo,
            Nome = "Admin Inativo",
            Email = "admin-inativo@teste.com",
            SenhaHash = HashAdminInativo,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        });
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.DeleteAsync($"/api/usuario/{IdAdmin}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.NotNull(await context.Usuarios.FindAsync(IdAdmin));
    }

    [Fact]
    public async Task DeleteUsuario_ComDoisAdminsAtivos_ExcluiUmComSucesso()
    {
        // A protecao vale so para o ultimo: com 2 Admins ativos a exclusao segue normal.
        using var factory = await CriarFactoryComDoisAdminsAtivosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.DeleteAsync($"/api/usuario/{IdSegundoAdmin}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Null(await context.Usuarios.FindAsync(IdSegundoAdmin));
        Assert.NotNull(await context.Usuarios.FindAsync(IdAdmin));
        Assert.Equal(1, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    [Fact]
    public async Task DeleteUsuario_UsuarioNaoAdmin_ContinuaSendoExcluido()
    {
        // A nova guarda nao pode afetar exclusao de usuarios comuns.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var response = await client.DeleteAsync($"/api/usuario/{IdAluno}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Null(await context.Usuarios.FindAsync(IdAluno));
    }

    [Fact]
    public async Task DeleteUsuario_ProfessorOrientaTcc_Retorna409ENaoRemove()
    {
        // FK_Tccs_usuarios_OrientadorId nao tem cascade: excluir sem checar
        // antes derrubaria em DbUpdateException (500) na FK do banco real.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();

        using (var setup = factory.CriarContextoDireto())
        {
            setup.Tccs.Add(new Tcc { Titulo = "TCC Teste", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor });
            await setup.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdProfessor}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.NotNull(await context.Usuarios.FindAsync(IdProfessor));
    }

    [Fact]
    public async Task DeleteUsuario_ProfessorParticipaDeBanca_Retorna409ENaoRemove()
    {
        // FK_BancaAvaliadores_usuarios_ProfessorId e Restrict: mesma protecao
        // acima, agora pelo vinculo de avaliador em banca.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();

        using (var setup = factory.CriarContextoDireto())
        {
            setup.BancaAvaliadores.Add(new BancaAvaliador { BancaId = 1, ProfessorId = IdProfessor });
            await setup.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");
        var response = await client.DeleteAsync($"/api/usuario/{IdProfessor}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.NotNull(await context.Usuarios.FindAsync(IdProfessor));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // #67 — PUT /api/usuario/{id}: bloqueio silencioso de Tipo/Ativo do ultimo Admin
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_TentandoVirarAluno_MantemTipoAdminEAplicaDemaisCampos()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Rebaixado",
            Email = "admin-rebaixado@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno, // deve ser ignorado
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(TipoUsuario.Admin, retornado!.Tipo);
        Assert.True(retornado.Ativo);
        Assert.Equal("Admin Rebaixado", retornado.Nome);
        Assert.Equal("admin-rebaixado@teste.com", retornado.Email);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal(TipoUsuario.Admin, persistido!.Tipo);   // campo sensivel preservado
        Assert.True(persistido.Ativo);
        Assert.Equal("Admin Rebaixado", persistido.Nome);     // demais campos aplicados
        Assert.Equal("admin-rebaixado@teste.com", persistido.Email);
        Assert.Equal(1, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_TentandoSeDesativar_MantemAtivoTrue()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Desativado",
            Email = "admin@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = false // deve ser ignorado
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.True(retornado!.Ativo);
        Assert.Equal(TipoUsuario.Admin, retornado.Tipo);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.True(persistido!.Ativo);
        Assert.Equal(TipoUsuario.Admin, persistido.Tipo);
        Assert.Equal("Admin Desativado", persistido.Nome);
    }

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_TentandoRebaixarEDesativar_MantemAmbosOsCampos()
    {
        // Combinacao dos dois campos sensiveis na mesma request.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Teste",
            Email = "admin@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Professor,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal(TipoUsuario.Admin, persistido!.Tipo);
        Assert.True(persistido.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_OutroAdminTentaRebaixaLo_MantemTipoAdmin()
    {
        // O bloqueio olha o ALVO, nao o solicitante: um Admin inativo (ou qualquer
        // token com role Admin) tambem nao consegue rebaixar o ultimo Admin ativo.
        using var factory = await CriarFactoryAsync(new Usuario
        {
            Id = IdAdminInativo,
            Nome = "Admin Inativo",
            Email = "admin-inativo@teste.com",
            SenhaHash = HashAdminInativo,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        });
        var client = factory.CreateClientAutenticado(IdAdminInativo, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Teste",
            Email = "admin@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal(TipoUsuario.Admin, persistido!.Tipo);
        Assert.True(persistido.Ativo);
    }

    [Fact]
    public async Task UpdateUsuario_ComDoisAdminsAtivos_RebaixandoOutroAdmin_AplicaMudanca()
    {
        // Com 2 Admins ativos a protecao nao deve travar a operacao legitima.
        using var factory = await CriarFactoryComDoisAdminsAtivosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdSegundoAdmin,
            Nome = "Segundo Admin",
            Email = "admin2@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Professor,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdSegundoAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(TipoUsuario.Professor, retornado!.Tipo);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdSegundoAdmin);
        Assert.Equal(TipoUsuario.Professor, persistido!.Tipo);
        Assert.True(persistido.Ativo);
        Assert.Equal(1, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    [Fact]
    public async Task UpdateUsuario_ComDoisAdminsAtivos_DesativandoOutroAdmin_AplicaMudanca()
    {
        using var factory = await CriarFactoryComDoisAdminsAtivosAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdSegundoAdmin,
            Nome = "Segundo Admin",
            Email = "admin2@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdSegundoAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdSegundoAdmin);
        Assert.False(persistido!.Ativo);
        Assert.Equal(TipoUsuario.Admin, persistido.Tipo);
        Assert.Equal(1, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_SemMudancaSensivel_AtualizaNormalmente()
    {
        // Cenario que NAO deve entrar no ramo de bloqueio: Tipo/Ativo iguais aos atuais.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Atualizado",
            Email = "admin-atualizado@teste.com",
            Senha = "senha-admin-nova-321",
            Tipo = TipoUsuario.Admin,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var retornado = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal("Admin Atualizado", retornado!.Nome);
        Assert.Equal("admin-atualizado@teste.com", retornado.Email);
        Assert.Equal(TipoUsuario.Admin, retornado.Tipo);
        Assert.True(retornado.Ativo);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal("Admin Atualizado", persistido!.Nome);
        Assert.Equal("admin-atualizado@teste.com", persistido.Email);
        Assert.Equal(TipoUsuario.Admin, persistido.Tipo);
        Assert.True(persistido.Ativo);
        Assert.True(BCrypt.Net.BCrypt.Verify("senha-admin-nova-321", persistido.SenhaHash));
    }

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_PromovendoOutroUsuarioAAdmin_ContinuaFuncionando()
    {
        // A guarda so protege o alvo que hoje e o unico Admin ativo — promover
        // outro usuario a Admin (saida natural do "unico admin") segue permitido.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdProfessor,
            Nome = "Professor Teste",
            Email = "professor@teste.com",
            Senha = string.Empty,
            Tipo = TipoUsuario.Admin,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdProfessor}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(TipoUsuario.Admin, (await context.Usuarios.FindAsync(IdProfessor))!.Tipo);
        Assert.Equal(2, context.Usuarios.Count(u => u.Tipo == TipoUsuario.Admin && u.Ativo));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Interacao entre as duas correcoes
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_UnicoAdminAtivo_EmailDuplicado_Retorna400ENaoAplicaNemBloqueioNemEdicao()
    {
        // A validacao de e-mail vem antes do tratamento de campos sensiveis:
        // a request inteira falha e o Admin permanece exatamente como estava.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAdmin,
            Nome = "Admin Alterado",
            Email = "aluno@teste.com", // pertence a IdAluno
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = false
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAdmin}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var persistido = await context.Usuarios.FindAsync(IdAdmin);
        Assert.Equal("Admin Teste", persistido!.Nome);
        Assert.Equal("admin@teste.com", persistido.Email);
        Assert.Equal(TipoUsuario.Admin, persistido.Tipo);
        Assert.True(persistido.Ativo);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UsuarioDtoValidator (achado de seguranca A10-2/A06-3): a coluna usuarios.Email
    // passou de nvarchar(max) para nvarchar(450) junto com o indice unico da #65.
    // Sem validacao, um e-mail maior que isso resultaria em SqlException (500) —
    // alcancavel por qualquer usuario autenticado via autoedicao. O validator
    // rejeita esses casos em 400 antes de a requisicao chegar ao controller.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsuario_EmailComExatamente450Caracteres_Retorna200()
    {
        // Guarda de regressao para o limite exato: MaximumLength(450) precisa
        // continuar alinhado a nvarchar(450) da coluna. Sem este teste, reduzir
        // o limite do validator (ex.: para 100) nao quebraria a suite.
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var emailNoLimite = new string('a', 440) + "@teste.com"; // 450 caracteres

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = emailNoLimite,
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(emailNoLimite, (await context.Usuarios.FindAsync(IdAluno))!.Email);
    }

    [Fact]
    public async Task UpdateUsuario_EmailComMaisDe450Caracteres_Retorna400ENaoAlteraNada()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var emailMuitoLongo = new string('a', 445) + "@teste.com"; // 455 caracteres

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = emailMuitoLongo,
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal("aluno@teste.com", (await context.Usuarios.FindAsync(IdAluno))!.Email);
    }

    [Fact]
    public async Task UpdateUsuario_EmailComFormatoInvalido_Retorna400ENaoAlteraNada()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "nao-e-um-email",
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal("aluno@teste.com", (await context.Usuarios.FindAsync(IdAluno))!.Email);
    }

    [Fact]
    public async Task CreateUsuario_EmailComMaisDe450Caracteres_Retorna400ENaoCriaUsuario()
    {
        using var factory = await CriarFactoryComUnicoAdminAtivoAsync();
        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var emailMuitoLongo = new string('b', 445) + "@teste.com"; // 455 caracteres

        var dto = new UsuarioDto
        {
            Nome = "Novo Usuario",
            Email = emailMuitoLongo,
            Senha = "senha-nova-123",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Usuarios.AnyAsync(u => u.Email == emailMuitoLongo));
    }
}
