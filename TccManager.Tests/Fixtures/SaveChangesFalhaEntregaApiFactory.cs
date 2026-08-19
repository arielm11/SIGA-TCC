using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Variante da <see cref="WebRootIsolatedApiFactory"/> que força o <c>SaveChangesAsync</c> a
/// falhar exatamente quando uma <see cref="Entrega"/> nova está sendo persistida, via
/// <c>ISaveChangesInterceptor</c>. É o único jeito limpo de exercitar a compensação de upload
/// órfão de <c>TccController.EnviarEntrega</c> (issue #69, item 4) no harness InMemory: o
/// arquivo já foi gravado em disco quando o banco falha, e o controller precisa removê-lo.
///
/// O interceptor só dispara para <c>Entrega</c> em estado <c>Added</c>, então a semeadura de
/// usuários/TCCs pelo <c>CriarContextoDireto()</c> continua funcionando normalmente.
/// </summary>
public class SaveChangesFalhaEntregaApiFactory : WebRootIsolatedApiFactory
{
    public const string MensagemDaFalha = "Falha simulada de SaveChangesAsync ao persistir a Entrega.";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // AddDbContext usa TryAdd para DbContextOptions<AppDbContext>: sem remover o
            // registro feito pela factory base, a segunda configuração seria ignorada.
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
                options.AddInterceptors(new FalhaAoSalvarEntregaInterceptor());
            });
        });
    }

    private sealed class FalhaAoSalvarEntregaInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var persistindoEntregaNova = eventData.Context?.ChangeTracker
                .Entries<Entrega>()
                .Any(e => e.State == EntityState.Added) == true;

            if (persistindoEntregaNova)
                throw new InvalidOperationException(MensagemDaFalha);

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
