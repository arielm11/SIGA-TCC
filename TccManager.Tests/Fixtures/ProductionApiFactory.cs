using Microsoft.AspNetCore.Hosting;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, seção 9 — "Nova
/// factory de ambiente não-Development só para afirmar Secure no Set-Cookie"). A
/// <see cref="TccApiFactory"/> sempre sobe em "Development" (<c>Secure=false</c> no cookie de
/// refresh, decisão 5.3). Esta variante troca o ambiente para "Production" DEPOIS do callback da
/// classe base (última escrita de <c>UseEnvironment</c> vence, mesmo padrão já usado por
/// <see cref="JwtBearerApiFactory"/> para sobrescrever <c>Jwt:Key</c>), preservando todo o resto
/// (banco InMemory isolado, Jwt:Key de teste).
/// </summary>
public class ProductionApiFactory : TccApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseEnvironment("Production");
    }
}
