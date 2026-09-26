using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #105 — variante da <see cref="WebRootIsolatedApiFactory"/> que força o
/// <c>SaveChangesAsync</c> a falhar exatamente quando uma <see cref="Banca"/> existente está
/// sendo editada, via <c>ISaveChangesInterceptor</c>. É o único jeito limpo de exercitar a
/// compensação de upload órfão de <c>CoordenadorController.RegistrarResultadoBanca</c> no
/// harness InMemory: o arquivo da ata já foi gravado em disco quando o banco falha, e o
/// controller precisa removê-lo. Mesmo padrão de <see cref="SaveChangesFalhaEntregaApiFactory"/>.
/// </summary>
public class SaveChangesFalhaBancaApiFactory : WebRootIsolatedApiFactory
{
    public const string MensagemDaFalha = "Falha simulada de SaveChangesAsync ao registrar o resultado da banca.";

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
                options.AddInterceptors(new FalhaAoSalvarBancaInterceptor());
            });
        });
    }

    private sealed class FalhaAoSalvarBancaInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var editandoBanca = eventData.Context?.ChangeTracker
                .Entries<Banca>()
                .Any(e => e.State == EntityState.Modified) == true;

            if (editandoBanca)
                throw new InvalidOperationException(MensagemDaFalha);

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
