using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #139 (achado M3) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateException"/> com uma
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> de violação de índice único (2601/2627)
/// exatamente quando uma <see cref="Banca"/> nova está sendo persistida, via
/// <c>ISaveChangesInterceptor</c>. Mesmo motivo de <see cref="SaveChangesFalhaConcorrenciaPropostaApiFactory"/>:
/// o provider InMemory não implementa índices únicos relacionais (P-04), então o
/// <c>catch (DbUpdateException) when (ex.InnerException is SqlException { Number: 2601 or 2627 })</c>
/// de <c>CoordenadorController.AgendarBanca</c> nunca é alcançado naturalmente nele.
/// </summary>
public class SaveChangesFalhaBancaDuplicadaApiFactory : TccApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
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
                options.AddInterceptors(new FalhaDeIndiceUnicoAoSalvarBancaInterceptor());
            });
        });
    }

    private sealed class FalhaDeIndiceUnicoAoSalvarBancaInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var criandoBancaNova = eventData.Context?.ChangeTracker
                .Entries<Banca>()
                .Any(e => e.State == EntityState.Added) == true;

            if (criandoBancaNova)
            {
                var sqlException = SqlExceptionSimulada.Criar(SqlExceptionSimulada.NumeroViolacaoChaveUnica);
                throw new DbUpdateException("Violação simulada do índice único Banca.TccId.", sqlException);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
