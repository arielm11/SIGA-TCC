using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #60 — hardening da validação do JWT no <c>Program.cs</c>:
/// (a) <c>ValidateIssuer</c>/<c>ValidateAudience</c> passaram de <c>false</c> para <c>true</c>;
/// (b) <c>ClockSkew</c> explícito de 30s no lugar do default de 5 min do framework;
/// (c) a chave é lida com <c>Encoding.UTF8</c> (antes <c>Encoding.ASCII</c>), tanto na emissão
///     (<c>TokenService</c>) quanto na validação.
///
/// Estes testes NÃO podem usar a <see cref="TccApiFactory"/> pura: ela substitui o scheme
/// JwtBearer por um handler de teste baseado em cabeçalhos, o que faria qualquer asserção
/// sobre validação de token passar por acidente. Por isso usam a
/// <see cref="JwtBearerApiFactory"/>, que mantém o pipeline real.
///
/// Endpoint alvo: <c>GET /api/usuario/me</c> — protegido apenas por <c>[Authorize]</c> (sem
/// exigência de role), então o status distingue com clareza "token rejeitado" (401) de
/// "token aceito" (200).
/// </summary>
public class Auth_JwtHardening_Tests
{
    private const string RotaProtegida = "/api/usuario/me";
    private const int IdUsuario = 77;

    /// <summary>
    /// Chave com caracteres fora do ASCII. É o cenário em que <c>Encoding.ASCII</c> e
    /// <c>Encoding.UTF8</c> produzem arrays de bytes DIFERENTES (o ASCII converte cada
    /// caractere não representável em '?', 0x3F, destruindo entropia da chave).
    /// </summary>
    private const string ChaveNaoAscii = "chave-de-teste-com-acentuacao-e-cedilha-çãõéêà-1234567890";

    private static async Task SemearUsuarioAsync(TccApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();
        context.Usuarios.Add(new Usuario
        {
            Id = IdUsuario,
            Nome = "Usuario Jwt",
            Email = "jwt@teste.com",
            SenhaHash = BCrypt.Net.BCrypt.HashPassword("SenhaValida123"),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Reproduz o formato de token emitido por <c>TokenService.GerarAccessToken</c>, mas com
    /// issuer/audience/validade/encoding da chave parametrizáveis, para poder produzir tokens
    /// deliberadamente inválidos sob cada regra de validação isoladamente.
    /// </summary>
    private static string GerarToken(
        string chave,
        string issuer,
        string audience,
        Encoding encoding,
        DateTime? expiraEmUtc = null)
    {
        var expiracao = expiraEmUtc ?? DateTime.UtcNow.AddMinutes(15);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, IdUsuario.ToString()),
                new Claim(ClaimTypes.Name, "Usuario Jwt"),
                new Claim(ClaimTypes.Email, "jwt@teste.com"),
                new Claim(ClaimTypes.Role, nameof(TipoUsuario.Aluno))
            }),
            // NotBefore/IssuedAt explícitos: sem isso o handler assume "agora" e um token
            // já expirado seria rejeitado na própria criação (nbf > exp).
            NotBefore = expiracao.AddMinutes(-15),
            IssuedAt = expiracao.AddMinutes(-15),
            Expires = expiracao,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(encoding.GetBytes(chave)),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static HttpClient CriarClienteComBearer(JwtBearerApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ───────────────────── Baseline: token totalmente válido ─────────────────────

    [Fact]
    public async Task TokenComIssuerEAudienceCorretos_EhAceito()
    {
        // Controle do experimento: sem ele, um 401 nos testes seguintes poderia ser causado
        // por qualquer outra coisa (chave errada, endpoint errado, scheme não ativo).
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            factory.Chave,
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<UsuarioDto>();
        Assert.Equal(IdUsuario, dto!.Id);
    }

    [Fact]
    public async Task SemToken_Retorna401()
    {
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var response = await factory.CreateClient().GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ───────────────────── ValidateIssuer / ValidateAudience ─────────────────────

    [Fact]
    public async Task TokenComIssuerDiferente_Retorna401()
    {
        // Com ValidateIssuer = false (código anterior) este token seria ACEITO: um invasor
        // que conhecesse a chave de outro sistema/ambiente conseguiria emitir tokens válidos.
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            factory.Chave,
            "EmissorFalso",
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenComAudienceDiferente_Retorna401()
    {
        // Com ValidateAudience = false (código anterior) um token emitido para OUTRA
        // aplicação (mesma chave, audiência distinta) seria aceito aqui.
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            factory.Chave,
            JwtBearerApiFactory.IssuerEsperado,
            "OutraAplicacao",
            Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenSemIssuerESemAudience_Retorna401()
    {
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(factory.Chave, issuer: null!, audience: null!, Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ───────────────────────────── ClockSkew ─────────────────────────────

    [Fact]
    public async Task TokenExpiradoHaDoisMinutos_Retorna401()
    {
        // Sob o ClockSkew default do framework (5 min) este token AINDA seria aceito.
        // Com ClockSkew = 30s ele já está fora da tolerância.
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            factory.Chave,
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8,
            expiraEmUtc: DateTime.UtcNow.AddMinutes(-2));

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenExpiradoHaPoucosSegundos_ContinuaAceitoDentroDaToleranciaDe30s()
    {
        // Complementa o teste acima: a tolerância não foi zerada (o que quebraria clientes
        // legítimos com relógio ligeiramente adiantado), apenas encurtada para 30s.
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            factory.Chave,
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8,
            expiraEmUtc: DateTime.UtcNow.AddSeconds(-5));

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ────────────────────── Encoding da chave (ASCII → UTF8) ──────────────────────

    [Fact]
    public async Task ChaveNaoAscii_TokenAssinadoComBytesAscii_Retorna401()
    {
        // Prova direta de que a API NÃO lê mais a chave como ASCII: com uma chave contendo
        // acentos, Encoding.ASCII.GetBytes produz bytes diferentes de Encoding.UTF8.GetBytes
        // (cada caractere não-ASCII vira '?'), logo a assinatura não confere.
        using var factory = new JwtBearerApiFactory(ChaveNaoAscii);
        await SemearUsuarioAsync(factory);

        Assert.NotEqual(Encoding.ASCII.GetBytes(ChaveNaoAscii), Encoding.UTF8.GetBytes(ChaveNaoAscii));

        var token = GerarToken(
            ChaveNaoAscii,
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.ASCII);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChaveNaoAscii_TokenAssinadoComBytesUtf8_EhAceito()
    {
        using var factory = new JwtBearerApiFactory(ChaveNaoAscii);
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            ChaveNaoAscii,
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ChaveNaoAscii_TokenEmitidoPeloProprioLogin_EhAceitoNaRotaProtegida()
    {
        // Coerência ponta a ponta entre emissão (TokenService, UTF8) e validação
        // (Program.cs, UTF8): o token devolvido pelo /api/auth/login precisa ser aceito
        // pelo pipeline de autenticação real mesmo com chave fora do ASCII.
        using var factory = new JwtBearerApiFactory(ChaveNaoAscii);
        await SemearUsuarioAsync(factory);

        var login = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login",
            new LoginDto { Email = "jwt@teste.com", Senha = "SenhaValida123" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var dto = await login.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(dto);

        var response = await CriarClienteComBearer(factory, dto!.Token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ──────────────────── Assinatura (regressão do que já existia) ────────────────────

    [Fact]
    public async Task TokenAssinadoComOutraChave_Retorna401()
    {
        using var factory = new JwtBearerApiFactory();
        await SemearUsuarioAsync(factory);

        var token = GerarToken(
            "outra-chave-completamente-diferente-com-256-bits-ok",
            JwtBearerApiFactory.IssuerEsperado,
            JwtBearerApiFactory.AudienceEsperada,
            Encoding.UTF8);

        var response = await CriarClienteComBearer(factory, token).GetAsync(RotaProtegida);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
