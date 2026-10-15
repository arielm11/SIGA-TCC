using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Variante da <see cref="TccApiFactory"/> que mantém o pipeline REAL de autenticação JWT
/// (<c>JwtBearerDefaults.AuthenticationScheme</c>) em vez do <c>TestAuthHandler</c>.
///
/// A <see cref="TccApiFactory"/> troca os schemes padrão por um scheme "Test" que injeta as
/// claims a partir de cabeçalhos — ótimo para testar autorização de controllers, mas
/// inservível para testar a VALIDAÇÃO do token (issue #60: ValidateIssuer/ValidateAudience,
/// ClockSkew e o encoding da chave). Esta factory desfaz somente essa troca de scheme,
/// reaproveitando todo o resto (banco InMemory isolado por instância, ambiente Development).
///
/// Issuer/Audience/Key são fixados aqui para que os testes possam assinar tokens
/// deliberadamente divergentes sem depender dos valores versionados no appsettings.json.
/// </summary>
public class JwtBearerApiFactory : TccApiFactory
{
    /// <summary>Chave HMAC padrão — ASCII puro, >= 256 bits, como exige o HmacSha256.</summary>
    public const string ChavePadrao = "chave-de-teste-super-secreta-para-jwt-1234567890";

    public const string IssuerEsperado = "TccManagerApiTeste";
    public const string AudienceEsperada = "TccManagerClientTeste";

    public JwtBearerApiFactory(string? chave = null)
    {
        Chave = chave ?? ChavePadrao;
    }

    /// <summary>Valor efetivo de <c>Jwt:Key</c> do host — usado pelos testes para assinar tokens.</summary>
    public string Chave { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Sobrescreve o Jwt:Key definido pela TccApiFactory (última escrita vence).
        builder.UseSetting("Jwt:Key", Chave);
        builder.UseSetting("Jwt:Issuer", IssuerEsperado);
        builder.UseSetting("Jwt:Audience", AudienceEsperada);

        // Este callback é registrado DEPOIS do da classe base, portanto a configuração de
        // AuthenticationOptions aplicada aqui é a última a rodar e volta os schemes padrão
        // para o JwtBearer registrado no Program.cs.
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            });
        });
    }
}
