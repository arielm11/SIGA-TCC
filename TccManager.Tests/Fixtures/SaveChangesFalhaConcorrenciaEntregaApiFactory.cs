using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #145 (achado B2) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateConcurrencyException"/> exatamente
/// quando uma <see cref="Entrega"/> existente está sendo editada, via
/// <c>ISaveChangesInterceptor</c>. Mesmo motivo de
/// <see cref="SaveChangesFalhaConcorrenciaPropostaApiFactory"/>: o provider InMemory não gera
/// um novo valor de <c>Entrega.RowVersion</c> a cada save, então a exceção nunca ocorre "de
/// verdade" nele.
/// </summary>
public class SaveChangesFalhaConcorrenciaEntregaApiFactory : TccApiFactory
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
                options.AddInterceptors(new FalhaDeConcorrenciaAoSalvarEntregaInterceptor());
            });
        });
    }

    private sealed class FalhaDeConcorrenciaAoSalvarEntregaInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var editandoEntrega = eventData.Context?.ChangeTracker
                .Entries<Entrega>()
                .Any(e => e.State == EntityState.Modified) == true;

            if (editandoEntrega)
                throw new DbUpdateConcurrencyException(
                    "Conflito de concorrência simulado: a entrega já foi alterada por outra requisição.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
