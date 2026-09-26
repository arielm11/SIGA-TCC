using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #91 (achado A08-1) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a falhar exatamente quando um <see cref="Usuario"/> está sendo
/// persistido (criado, editado OU excluído), via <c>ISaveChangesInterceptor</c>. É o único jeito limpo
/// de exercitar os catches de <c>UsuarioController</c> que dependem de uma
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> real no harness InMemory — que não
/// aplica o índice único de e-mail nem FKs (P-04 da arquitetura, mesma limitação documentada
/// em <c>UsuarioController_EmailUnicoEUltimoAdmin_Tests.InsercaoDiretaDeEmailDuplicado_...</c>).
/// Mesmo padrão já usado em <see cref="SaveChangesFalhaEntregaApiFactory"/> e
/// <see cref="BootstrapAdminApiFactory"/>.
///
/// A exceção injetada é configurável no construtor (não fixa em 2601/2627 como no bootstrap):
/// serve tanto para o caminho de unicidade de e-mail (POST/PUT, catch específico por Number)
/// quanto para o backstop genérico de integridade referencial do DELETE (catch por qualquer
/// SqlException).
/// </summary>
public class SaveChangesFalhaUsuarioApiFactory : TccApiFactory
{
    private readonly Exception _falha;

    public SaveChangesFalhaUsuarioApiFactory(Exception falha)
    {
        _falha = falha;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // AddDbContext usa TryAdd para DbContextOptions<AppDbContext>: sem remover o
            // registro feito pela factory base, esta segunda configuração seria ignorada.
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
                options.UseInMemoryDatabase(DbName);
                options.AddInterceptors(new FalhaAoSalvarUsuarioInterceptor(_falha));
            });
        });
    }

    /// <summary>
    /// Contexto para SEMEAR o banco antes da requisição de teste, sem passar pelo
    /// interceptor (que dispararia a falha simulada já na semeadura). Aponta para o mesmo
    /// store InMemory (mesmo <c>DbName</c>, raiz padrão do provider) — só não tem o
    /// interceptor registrado.
    /// </summary>
    public AppDbContext NovoContextoSemInterceptor() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(DbName)
            .Options);

    private sealed class FalhaAoSalvarUsuarioInterceptor : SaveChangesInterceptor
    {
        private readonly Exception _falha;

        public FalhaAoSalvarUsuarioInterceptor(Exception falha) => _falha = falha;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var persistindoUsuario = eventData.Context?.ChangeTracker
                .Entries<Usuario>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) == true;

            if (persistindoUsuario)
                throw _falha;

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
