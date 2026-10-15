using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #88 — bootstrap do primeiro Admin via configuração de startup
/// (<c>AdminBootstrapSetup.ExecutarBootstrapAdminAsync</c>, D1-D5 da arquitetura).
///
/// Cobre T1-T6 da seção 12.2 da arquitetura mais os caminhos de log de auditoria (RF-05, A1-A4).
/// Cada teste sobe seu próprio host (<see cref="BootstrapAdminApiFactory"/>), porque o bootstrap
/// só roda uma vez, no startup: é a única forma de exercitá-lo.
///
/// Sobre a corrida entre instâncias (2601/2627): o provider InMemory não implementa índices
/// únicos relacionais, então a exceção é injetada por interceptor com uma
/// <c>SqlException</c> fabricada (<see cref="SqlExceptionSimulada"/>) — ver a nota de limite lá.
/// </summary>
public class AdminBootstrapTests
{
    private const string EmailBootstrap = "admin.bootstrap@teste.com";
    private const string SenhaBootstrap = "SenhaDeBootstrap#2026";

    // ─────────────────────────── helpers de log ───────────────────────────

    private static IReadOnlyList<LogEvent> EventosComTexto(BootstrapAdminApiFactory factory, string trecho) =>
        factory.LogsDoHost
            .Where(evento => evento.RenderMessage().Contains(trecho, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static LogEvent EventoDeAuditoria(BootstrapAdminApiFactory factory, string trecho) =>
        Assert.Single(EventosComTexto(factory, trecho));

    private static string MotivoDe(LogEvent evento)
    {
        Assert.True(evento.Properties.ContainsKey("Motivo"),
            $"O evento '{evento.RenderMessage()}' deveria trazer a propriedade estruturada Motivo.");
        return evento.Properties["Motivo"].ToString();
    }

    /// <summary>
    /// RNF-03/D11: nenhum log do startup pode conter a senha configurada nem o e-mail de
    /// bootstrap — nem na mensagem renderizada, nem em nenhuma propriedade estruturada.
    /// </summary>
    private static void AssertNenhumLogVazaCredencial(BootstrapAdminApiFactory factory)
    {
        foreach (var evento in factory.LogsDoHost)
        {
            var textoCompleto = evento.RenderMessage() + " " +
                                string.Join(" ", evento.Properties.Select(p => $"{p.Key}={p.Value}"));

            Assert.DoesNotContain(SenhaBootstrap, textoCompleto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EmailBootstrap, textoCompleto, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Usuario AdminAtivo(string email = "admin.existente@teste.com") => new()
    {
        Nome = "Admin Existente",
        Email = email,
        SenhaHash = BCrypt.Net.BCrypt.HashPassword("SenhaDoAdminExistente1"),
        Tipo = TipoUsuario.Admin,
        Ativo = true
    };

    // ─────────────────── T3: configuração ausente = caminho inerte ───────────────────

    [Fact]
    public async Task ConfiguracaoAusente_NaoCriaNenhumUsuarioENaoLogaAuditoriaDeBootstrap()
    {
        using var factory = new BootstrapAdminApiFactory();

        factory.CreateClient(); // dispara a construção e a inicialização do host

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());
        Assert.Empty(EventosComTexto(factory, "bootstrap"));
    }

    [Fact]
    public void ConfiguracaoAusente_NemChegaAResolverOAppDbContext()
    {
        // O passo (1) do AdminBootstrapSetup precisa sair ANTES de CreateScope()/
        // GetRequiredService<AppDbContext>() — é o que garante custo zero para todo ambiente
        // (e para os 978 testes existentes) que não configura o bootstrap. Aqui a resolução do
        // AppDbContext é sabotada: se ela acontecesse, o startup quebraria.
        using var factory = new FactoryComAppDbContextInutilizavel(configurarBootstrap: false);

        var excecao = Record.Exception(() => factory.CreateClient());

        Assert.Null(excecao);
    }

    [Fact]
    public void ConfiguracaoPresente_ResolveOAppDbContext()
    {
        // Contraprova do teste anterior: com a configuração presente o bootstrap TEM que
        // tocar o banco. Sem esta contraprova, o teste acima passaria mesmo se o AppDbContext
        // nunca fosse sabotado de verdade.
        using var factory = new FactoryComAppDbContextInutilizavel(configurarBootstrap: true);

        var excecao = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(excecao);
        Assert.Contains(
            FactoryComAppDbContextInutilizavel.Marcador,
            excecao!.ToString(),
            StringComparison.Ordinal);
    }

    // ─────────────────── T1: caminho feliz ───────────────────

    [Fact]
    public async Task ConfiguracaoCompletaEZeroAdminsAtivos_CriaAdminComPrecisaTrocarSenhaLigada()
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var admin = Assert.Single(await contexto.Usuarios.ToListAsync());

        Assert.Equal(EmailBootstrap, admin.Email);
        Assert.Equal(TipoUsuario.Admin, admin.Tipo);
        Assert.True(admin.Ativo);
        Assert.True(admin.PrecisaTrocarSenha);
        Assert.True(BCrypt.Net.BCrypt.Verify(SenhaBootstrap, admin.SenhaHash));
        Assert.NotEqual(SenhaBootstrap, admin.SenhaHash);
    }

    [Fact]
    public void AdminCriado_LogaA1ComUsuarioIdEMecanismo_SemCredenciais()
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);

        factory.CreateClient();

        var evento = EventoDeAuditoria(factory, "BOOTSTRAP DE ADMIN EXECUTADO");
        Assert.Equal(LogEventLevel.Warning, evento.Level);
        Assert.True(evento.Properties.ContainsKey("UsuarioId"));
        Assert.Contains("configuracao-startup", evento.Properties["Mecanismo"].ToString(), StringComparison.Ordinal);

        AssertNenhumLogVazaCredencial(factory);
    }

    [Fact]
    public async Task SemNomeConfigurado_UsaONomePadraoAdministrador()
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var admin = await contexto.Usuarios.SingleAsync();
        Assert.Equal("Administrador", admin.Nome);
    }

    [Fact]
    public async Task NomeConfigurado_EhUsadoENuncaExcede200Caracteres()
    {
        var nomeLongo = new string('N', 250);
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap, nomeLongo);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var admin = await contexto.Usuarios.SingleAsync();
        Assert.Equal(200, admin.Nome.Length);
        Assert.Equal(new string('N', 200), admin.Nome);
    }

    [Fact]
    public async Task AdminCriadoPeloBootstrap_NaoConsegueLogar_ERecebe403()
    {
        // Amarra o bootstrap ao D8: a credencial configurada autentica, mas não vira sessão.
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);
        var client = factory.CreateClient();

        var resposta = await client.PostAsJsonAsync("/api/auth/login",
            new LoginDto { Email = EmailBootstrap, Senha = SenhaBootstrap });

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.RefreshTokens.ToListAsync());
    }

    // ─────────────────── T2/D4: já existe Admin ativo ───────────────────

    [Fact]
    public async Task JaExisteAdminAtivo_NaoCriaNadaELogaWarningComOTotal()
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);
        factory.SemearAntesDoStartup(AdminAtivo());

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var usuario = Assert.Single(await contexto.Usuarios.ToListAsync());
        Assert.Equal("admin.existente@teste.com", usuario.Email);
        Assert.False(usuario.PrecisaTrocarSenha);

        var evento = EventoDeAuditoria(factory, "já tem");
        Assert.Equal(LogEventLevel.Warning, evento.Level);
        Assert.Equal("1", evento.Properties["TotalAdminsAtivos"].ToString());
        AssertNenhumLogVazaCredencial(factory);
    }

    [Fact]
    public async Task JaExisteAdminAtivo_NaoPromoveUsuarioExistenteNemAlteraSenha()
    {
        // D4: a variável de configuração NÃO pode ser uma alavanca de escalação de privilégio
        // sobre contas existentes.
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);
        var aluno = new Usuario
        {
            Nome = "Aluno Comum",
            Email = EmailBootstrap, // exatamente o e-mail configurado no bootstrap
            SenhaHash = BCrypt.Net.BCrypt.HashPassword("SenhaOriginalDoAluno1"),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };
        factory.SemearAntesDoStartup(AdminAtivo(), aluno);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var alunoDepois = await contexto.Usuarios.SingleAsync(u => u.Email == EmailBootstrap);
        Assert.Equal(TipoUsuario.Aluno, alunoDepois.Tipo);
        Assert.False(alunoDepois.PrecisaTrocarSenha);
        Assert.True(BCrypt.Net.BCrypt.Verify("SenhaOriginalDoAluno1", alunoDepois.SenhaHash));
        Assert.Equal(2, await contexto.Usuarios.CountAsync());
    }

    [Fact]
    public async Task AdminExistenteInativo_ContaComoZeroAdminsAtivos_CriaNovoSemReativarOAntigo()
    {
        // Cenário A06-2 (o único Admin foi desativado): o bootstrap repara criando uma conta
        // nova — nunca reativando a antiga (D4). O resultado com DUAS contas de Admin (uma
        // inativa) é intencional.
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);
        var adminInativo = AdminAtivo("admin.desativado@teste.com");
        adminInativo.Ativo = false;
        factory.SemearAntesDoStartup(adminInativo);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Equal(2, await contexto.Usuarios.CountAsync());

        var antigo = await contexto.Usuarios.SingleAsync(u => u.Email == "admin.desativado@teste.com");
        Assert.False(antigo.Ativo);

        var novo = await contexto.Usuarios.SingleAsync(u => u.Email == EmailBootstrap);
        Assert.True(novo.Ativo);
        Assert.True(novo.PrecisaTrocarSenha);
    }

    // ─────────────────── T5/T6: configuração recusada ───────────────────

    [Fact]
    public async Task SomenteOEmailConfigurado_RecusaComConfiguracaoIncompleta()
    {
        using var factory = new BootstrapAdminApiFactory(email: EmailBootstrap, senha: null);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());

        var evento = EventoDeAuditoria(factory, "Bootstrap de Admin recusado");
        Assert.Equal(LogEventLevel.Error, evento.Level);
        Assert.Contains("ConfiguracaoIncompleta", MotivoDe(evento), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomenteASenhaConfigurada_RecusaComConfiguracaoIncompleta()
    {
        using var factory = new BootstrapAdminApiFactory(email: null, senha: SenhaBootstrap);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());
        Assert.Contains(
            "ConfiguracaoIncompleta",
            MotivoDe(EventoDeAuditoria(factory, "Bootstrap de Admin recusado")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("curta1")]                 // < 12 caracteres
    [InlineData("            ")]           // só espaços
    [InlineData(EmailBootstrap)]           // igual ao e-mail configurado
    public async Task SenhaForaDaPolitica_NaoCriaAdminERegistraOMotivo(string senha)
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, senha);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());

        var evento = EventoDeAuditoria(factory, "Bootstrap de Admin recusado");
        Assert.Equal(LogEventLevel.Error, evento.Level);
        // "            " (só espaços) é tratado como configuração ausente pelo IsNullOrWhiteSpace
        // do passo 1b, e não chega na política — os dois motivos são aceitáveis aqui, o que não
        // pode acontecer é criar o Admin.
        Assert.Contains(
            MotivoDe(evento).Trim('"'),
            new[] { "SenhaForaDaPolitica", "ConfiguracaoIncompleta" });
    }

    [Fact]
    public async Task SenhaCurta_RecusaEspecificamentePorPoliticaDeSenha()
    {
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, "curta1");

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());

        var evento = EventoDeAuditoria(factory, "Bootstrap de Admin recusado");
        Assert.Contains("SenhaForaDaPolitica", MotivoDe(evento), StringComparison.Ordinal);
        // O motivo detalhado da política pode aparecer, mas nunca o valor da senha.
        Assert.DoesNotContain("curta1", evento.RenderMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nao-e-um-email")]
    [InlineData("sem-arroba.teste.com")]
    [InlineData("@dominio-sem-parte-local.com")]
    public async Task EmailInvalido_NaoCriaAdmin(string email)
    {
        using var factory = new BootstrapAdminApiFactory(email, SenhaBootstrap);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());
        Assert.Contains(
            "EmailInvalido",
            MotivoDe(EventoDeAuditoria(factory, "Bootstrap de Admin recusado")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailAcimaDe450Caracteres_EhRecusadoComoInvalido()
    {
        var emailEnorme = new string('a', 445) + "@teste.com";
        using var factory = new BootstrapAdminApiFactory(emailEnorme, SenhaBootstrap);

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());
        Assert.Contains(
            "EmailInvalido",
            MotivoDe(EventoDeAuditoria(factory, "Bootstrap de Admin recusado")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailJaPertenceAUsuarioExistente_NaoCriaNadaENaoLancaExcecao()
    {
        // T6 com zero Admins ativos: o pre-check de aplicação (passo 4) é o que barra, não o
        // índice único — o InMemory não o implementa.
        using var factory = new BootstrapAdminApiFactory(EmailBootstrap, SenhaBootstrap);
        factory.SemearAntesDoStartup(new Usuario
        {
            Nome = "Professor Qualquer",
            Email = EmailBootstrap,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword("SenhaDoProfessor12345"),
            Tipo = TipoUsuario.Professor,
            Ativo = true
        });

        factory.CreateClient();

        using var contexto = factory.NovoContexto();
        var usuario = Assert.Single(await contexto.Usuarios.ToListAsync());
        Assert.Equal(TipoUsuario.Professor, usuario.Tipo);

        var evento = EventoDeAuditoria(factory, "Bootstrap de Admin recusado");
        Assert.Equal(LogEventLevel.Error, evento.Level);
        Assert.Contains("EmailJaEmUso", MotivoDe(evento), StringComparison.Ordinal);
        AssertNenhumLogVazaCredencial(factory);
    }

    // ─────────────────── Corrida entre instâncias (2601/2627) ───────────────────

    [Theory]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoIndiceUnico)]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoChaveUnica)]
    public async Task ViolacaoDoIndiceUnicoDeEmail_EhTratadaComoBootstrapConcorrente(int numeroDoErro)
    {
        var sqlException = SqlExceptionSimulada.Criar(numeroDoErro);
        // Guarda contra falso positivo: se a fabricação por reflexão degradar numa atualização
        // do driver, o teste falha aqui, e não silenciosamente no caminho genérico de catch.
        Assert.Equal(numeroDoErro, sqlException.Number);

        using var factory = new BootstrapAdminApiFactory(
            EmailBootstrap,
            SenhaBootstrap,
            falhaAoSalvarUsuario: new DbUpdateException("violação simulada de índice único", sqlException));

        var client = factory.CreateClient();

        using var contexto = factory.NovoContexto();
        Assert.Empty(await contexto.Usuarios.ToListAsync());

        var evento = EventoDeAuditoria(factory, "Bootstrap de Admin concorrente");
        Assert.Equal(LogEventLevel.Warning, evento.Level);
        Assert.Empty(EventosComTexto(factory, "Falha ao executar o bootstrap"));

        // A API continua atendendo: a corrida não é erro (D3/D5).
        var resposta = await client.GetAsync("/api/usuario/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task FalhaGenericaDeBanco_NaoDerrubaOStartupELogaError()
    {
        // D5: bootstrap é tarefa operacional opcional — uma falha nele nunca pode virar
        // indisponibilidade da API.
        using var factory = new BootstrapAdminApiFactory(
            EmailBootstrap,
            SenhaBootstrap,
            falhaAoSalvarUsuario: new InvalidOperationException("falha simulada de banco"));

        var client = factory.CreateClient();

        var evento = EventoDeAuditoria(factory, "Falha ao executar o bootstrap");
        Assert.Equal(LogEventLevel.Error, evento.Level);
        Assert.DoesNotContain("falha simulada de banco", evento.RenderMessage(), StringComparison.Ordinal);
        AssertNenhumLogVazaCredencial(factory);

        var resposta = await client.GetAsync("/api/usuario/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    /// <summary>
    /// Sabota a resolução do <see cref="AppDbContext"/> no contêiner. Como
    /// <c>CreateScope()/GetRequiredService&lt;AppDbContext&gt;()</c> acontece FORA do try do
    /// <c>AdminBootstrapSetup</c>, qualquer acesso ao banco no startup vira falha de
    /// inicialização observável — o que transforma "não tocou o banco" numa asserção real, e
    /// não numa inferência.
    /// </summary>
    private sealed class FactoryComAppDbContextInutilizavel : TccApiFactory
    {
        public const string Marcador = "appdbcontext-inutilizavel-issue-88";

        private readonly bool _configurarBootstrap;

        public FactoryComAppDbContextInutilizavel(bool configurarBootstrap) =>
            _configurarBootstrap = configurarBootstrap;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting(BootstrapAdminApiFactory.ChaveEmail,
                _configurarBootstrap ? EmailBootstrap : string.Empty);
            builder.UseSetting(BootstrapAdminApiFactory.ChaveSenha,
                _configurarBootstrap ? SenhaBootstrap : string.Empty);

            builder.ConfigureServices(services =>
            {
                var descritores = services
                    .Where(d => d.ServiceType == typeof(AppDbContext))
                    .ToList();

                foreach (var d in descritores)
                {
                    services.Remove(d);
                }

                services.AddScoped<AppDbContext>(_ => throw new InvalidOperationException(Marcador));
            });
        }
    }
}
