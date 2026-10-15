using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #113 (achado A06-1) — variante da <see cref="TccApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateConcurrencyException"/> exatamente
/// quando um <see cref="Tcc"/> existente está sendo editado, via <c>ISaveChangesInterceptor</c>.
/// É o único jeito limpo de exercitar o catch de concorrência de
/// <c>CoordenadorController.DesignarOrientador</c>/<c>RejeitarProposta</c> no harness InMemory:
/// o provider não gera um novo valor de <c>RowVersion</c> a cada save (ver
/// <see cref="TccManager.Tests.Data.TccConcorrenciaOtimistaTests"/> para a limitação
/// documentada), então a exceção nunca ocorre "de verdade" nele. Mesmo padrão de
/// <see cref="SaveChangesFalhaEntregaApiFactory"/>/<see cref="SaveChangesFalhaBancaApiFactory"/>.
/// </summary>
public class SaveChangesFalhaConcorrenciaPropostaApiFactory : TccApiFactory
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
                options.AddInterceptors(new FalhaDeConcorrenciaAoSalvarTccInterceptor());
            });
        });
    }

    private sealed class FalhaDeConcorrenciaAoSalvarTccInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var editandoTcc = eventData.Context?.ChangeTracker
                .Entries<Tcc>()
                .Any(e => e.State == EntityState.Modified) == true;

            if (editandoTcc)
                throw new DbUpdateConcurrencyException(
                    "Conflito de concorrência simulado: a proposta já foi decidida por outra requisição.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
