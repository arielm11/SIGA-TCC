using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #145 (achado D6) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateConcurrencyException"/> exatamente
/// quando um <see cref="Tcc"/> está sendo EXCLUÍDO (<c>TccController.ExcluirProposta</c>), via
/// <c>ISaveChangesInterceptor</c>. Distinto de
/// <see cref="SaveChangesFalhaConcorrenciaPropostaApiFactory"/>, que dispara em
/// <c>EntityState.Modified</c> — aqui o estado relevante é <c>Deleted</c>.
/// </summary>
public class SaveChangesFalhaConcorrenciaExclusaoPropostaApiFactory : TccApiFactory
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
                options.AddInterceptors(new FalhaDeConcorrenciaAoExcluirTccInterceptor());
            });
        });
    }

    private sealed class FalhaDeConcorrenciaAoExcluirTccInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var excluindoTcc = eventData.Context?.ChangeTracker
                .Entries<Tcc>()
                .Any(e => e.State == EntityState.Deleted) == true;

            if (excluindoTcc)
                throw new DbUpdateConcurrencyException(
                    "Conflito de concorrência simulado: a proposta já foi decidida por outra requisição.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
