using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #145 (achado B1) — variante de <see cref="WebRootIsolatedApiFactory"/> (precisa do
/// WebRoot isolado porque <c>RegistrarResultadoBanca</c> grava a ata em disco) que força o
/// <c>SaveChangesAsync</c> a lançar <see cref="DbUpdateConcurrencyException"/> exatamente
/// quando um <see cref="Tcc"/> existente está sendo editado — mesmo mecanismo de
/// <see cref="SaveChangesFalhaConcorrenciaPropostaApiFactory"/>, reaproveitado aqui porque
/// aquela herda de <see cref="TccApiFactory"/> (sem WebRoot isolado) e este cenário precisa
/// do upload funcionar normalmente até o ponto do SaveChanges falhar.
/// </summary>
public class SaveChangesFalhaConcorrenciaBancaApiFactory : WebRootIsolatedApiFactory
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
                    "Conflito de concorrência simulado: o TCC já foi alterado por outra requisição.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
