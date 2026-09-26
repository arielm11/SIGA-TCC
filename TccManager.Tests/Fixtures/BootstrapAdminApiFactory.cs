using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #88 — factory para os testes do bootstrap de Admin (<c>AdminBootstrapSetup</c>).
///
/// Dois problemas que a <see cref="TccApiFactory"/> sozinha não resolve:
///
/// 1. <b>Semear ANTES do startup.</b> O bootstrap roda entre <c>builder.Build()</c> e o
///    pipeline HTTP, ou seja, antes de qualquer chamada de teste. Para exercitar "já existe
///    Admin ativo" (RF-02) o banco precisa estar populado quando o host sobe — e o
///    <c>CriarContextoDireto()</c> só existe depois disso. A solução é um
///    <see cref="InMemoryDatabaseRoot"/> explícito, compartilhado entre o contexto de
///    semeadura (criado à mão, sem host) e o registro do <see cref="AppDbContext"/> no
///    contêiner: os dois passam a enxergar exatamente o mesmo store, sem depender do cache
///    interno de service providers do EF.
/// 2. <b>Simular a corrida entre instâncias.</b> O provider InMemory não implementa índices
///    únicos relacionais, então o caminho <c>catch (DbUpdateException) when (SqlException
///    2601/2627)</c> nunca é alcançado naturalmente (P-04 da arquitetura). Um
///    <c>ISaveChangesInterceptor</c> injeta a exceção exata que o SQL Server produziria —
///    mesmo padrão já usado em <see cref="SaveChangesFalhaEntregaApiFactory"/>.
///
/// A captura de log vem da <see cref="ConfiguracaoCustomizadaApiFactory"/> (base), que
/// registra um sink Serilog no contêiner — é por onde os eventos de auditoria emitidos no
/// startup (A1 a A4) são observados.
/// </summary>
public class BootstrapAdminApiFactory : ConfiguracaoCustomizadaApiFactory
{
    public const string ChaveEmail = "Admin:BootstrapEmail";
    public const string ChaveSenha = "Admin:BootstrapSenha";
    public const string ChaveNome = "Admin:BootstrapNome";

    // Raiz ESTÁTICA de propósito: o isolamento entre testes já vem do DbName (um Guid por
    // factory). Uma raiz por instância faria o EF construir um service provider interno novo
    // para cada factory e estouraria o ManyServiceProvidersCreatedWarning (que é lançado como
    // erro a partir de 20 provedores) numa suíte com dezenas de hosts.
    private static readonly InMemoryDatabaseRoot _raiz = new();

    private readonly Exception? _falhaAoSalvarUsuario;

    /// <param name="email">valor de <c>Admin:BootstrapEmail</c> (<c>null</c> = chave vazia, como no appsettings versionado).</param>
    /// <param name="senha">valor de <c>Admin:BootstrapSenha</c>.</param>
    /// <param name="nome">valor de <c>Admin:BootstrapNome</c>.</param>
    /// <param name="falhaAoSalvarUsuario">
    /// quando informada, é lançada no <c>SaveChangesAsync</c> que persiste um
    /// <see cref="Usuario"/> novo — usada para simular a violação do índice único de e-mail.
    /// </param>
    public BootstrapAdminApiFactory(
        string? email = null,
        string? senha = null,
        string? nome = null,
        Exception? falhaAoSalvarUsuario = null)
        : base(MontarConfiguracao(email, senha, nome))
    {
        _falhaAoSalvarUsuario = falhaAoSalvarUsuario;
    }

    private static Dictionary<string, string> MontarConfiguracao(string? email, string? senha, string? nome) =>
        new()
        {
            // String vazia é como as chaves aparecem no appsettings.json versionado — o
            // bootstrap trata vazio/só-espaço como "não configurado".
            [ChaveEmail] = email ?? string.Empty,
            [ChaveSenha] = senha ?? string.Empty,
            [ChaveNome] = nome ?? string.Empty
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // AddDbContext usa TryAdd para DbContextOptions<AppDbContext>: sem remover o
            // registro da factory base, esta segunda configuração seria ignorada.
            var descritoresEf = services
                .Where(d => d.ServiceType.FullName != null &&
                            d.ServiceType.FullName.Contains("DbContextOptions"))
                .ToList();

            foreach (var d in descritoresEf)
            {
                services.Remove(d);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(DbName, _raiz);

                if (_falhaAoSalvarUsuario != null)
                    options.AddInterceptors(new FalhaAoSalvarUsuarioInterceptor(_falhaAoSalvarUsuario));
            });
        });
    }

    /// <summary>
    /// Contexto ligado ao MESMO store InMemory do host, criado sem passar pelo contêiner —
    /// pode ser usado antes de <c>CreateClient()</c> (semeadura pré-startup) e depois
    /// (asserções), inclusive quando o host tem o interceptor de falha ativo.
    /// </summary>
    public AppDbContext NovoContexto() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(DbName, _raiz)
            .Options);

    /// <summary>Popula o banco antes de o host subir (e, portanto, antes do bootstrap rodar).</summary>
    public void SemearAntesDoStartup(params Usuario[] usuarios)
    {
        using var contexto = NovoContexto();
        contexto.Usuarios.AddRange(usuarios);
        contexto.SaveChanges();
    }

    private sealed class FalhaAoSalvarUsuarioInterceptor : SaveChangesInterceptor
    {
        private readonly Exception _falha;

        public FalhaAoSalvarUsuarioInterceptor(Exception falha) => _falha = falha;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var persistindoUsuarioNovo = eventData.Context?.ChangeTracker
                .Entries<Usuario>()
                .Any(e => e.State == EntityState.Added) == true;

            if (persistindoUsuarioNovo)
                throw _falha;

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
