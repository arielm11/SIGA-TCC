using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #145 (achado B11) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateException"/> com uma
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> de violação de índice único (2601/2627)
/// exatamente quando um <see cref="Tcc"/> novo está sendo persistido
/// (<c>TccController.SubmeterProposta</c>), via <c>ISaveChangesInterceptor</c>. Mesmo padrão de
/// <see cref="SaveChangesFalhaBancaDuplicadaApiFactory"/>, trocando a entidade/estado
/// observado.
/// </summary>
public class SaveChangesFalhaTccDuplicadoApiFactory : TccApiFactory
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
                options.AddInterceptors(new FalhaDeIndiceUnicoAoSalvarTccInterceptor());
            });
        });
    }

    private sealed class FalhaDeIndiceUnicoAoSalvarTccInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var criandoTccNovo = eventData.Context?.ChangeTracker
                .Entries<Tcc>()
                .Any(e => e.State == EntityState.Added) == true;

            if (criandoTccNovo)
            {
                var sqlException = SqlExceptionSimulada.Criar(SqlExceptionSimulada.NumeroViolacaoChaveUnica);
                throw new DbUpdateException("Violação simulada do índice único Tccs.AlunoId.", sqlException);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
