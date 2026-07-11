using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Testes de integração da política de rate limiting "login" (S2).
///
/// Isolamento entre casos: cada teste cria sua própria instância de
/// <see cref="TccApiFactory"/> (portanto seu próprio host e sua própria instância do
/// PartitionedRateLimiter registrada na DI daquele host). Como o limitador é um serviço
/// por host, o estado de contagem NÃO é compartilhado entre factories distintas — o que
/// garante que o limite consumido em um teste não interfira em outro.
/// </summary>
public class RateLimitingLoginTests
{
    private const string RotaLogin = "/api/auth/login";
    private const string SenhaValida = "SenhaValida123";

    private static LoginDto CredencialInvalida() =>
        new() { Email = "naoexiste@teste.com", Senha = "errada" };

    private static async Task SemearUsuarioValido(TccApiFactory factory, string email)
    {
        using var context = factory.CriarContextoDireto();
        context.Usuarios.Add(new Usuario
        {
            Nome = "Usuario Valido",
            Email = email,
            SenhaHash = BCrypt.Net.BCrypt.HashPassword(SenhaValida),
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SextaTentativaDentroDaJanela_DeveRetornar429ComRetryAfter()
    {
        // Arrange
        var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var dto = CredencialInvalida();

        // Act — as 5 primeiras tentativas passam pelo limitador e chegam na lógica de auth
        for (var i = 1; i <= 5; i++)
        {
            var permitida = await client.PostAsJsonAsync(RotaLogin, dto);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        // 6ª tentativa dentro da mesma janela → deve ser bloqueada
        var bloqueada = await client.PostAsJsonAsync(RotaLogin, dto);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");

        var retryAfter = bloqueada.Headers.GetValues("Retry-After").First();
        Assert.True(int.TryParse(retryAfter, out var segundos),
            $"Retry-After deveria ser um inteiro em segundos, mas foi '{retryAfter}'.");
        Assert.InRange(segundos, 1, 60);
    }

    [Fact]
    public async Task CredenciaisValidasEInvalidas_ContamIgualmenteParaOLimite()
    {
        // Arrange — o rate limiter atua ANTES da lógica de autenticação, logo tanto um 200
        // (login válido) quanto um 401 (credencial inválida) consomem uma permissão.
        var factory = new TccApiFactory();
        await SemearUsuarioValido(factory, "valido@teste.com");
        var client = factory.CreateClient();

        var valida = new LoginDto { Email = "valido@teste.com", Senha = SenhaValida };
        var invalida = CredencialInvalida();

        // Act — 5 requisições intercalando válidas e inválidas
        var sequencia = new[] { valida, invalida, valida, invalida, valida };
        var statusObtidos = new List<HttpStatusCode>();
        foreach (var corpo in sequencia)
        {
            var resp = await client.PostAsJsonAsync(RotaLogin, corpo);
            statusObtidos.Add(resp.StatusCode);
        }

        // 6ª requisição (uma credencial VÁLIDA) já deve ser bloqueada, provando que os
        // sucessos anteriores também contaram para o limite.
        var sextaValida = await client.PostAsJsonAsync(RotaLogin, valida);

        // Assert
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusObtidos);
        Assert.Contains(HttpStatusCode.OK, statusObtidos);            // ao menos um login válido passou (200)
        Assert.Contains(HttpStatusCode.Unauthorized, statusObtidos);  // ao menos uma credencial inválida passou (401)
        Assert.Equal(HttpStatusCode.TooManyRequests, sextaValida.StatusCode);
    }

    [Fact]
    public async Task DentroDoLimite_CincoTentativas_NenhumaEhBloqueada()
    {
        // Arrange
        var factory = new TccApiFactory();
        var client = factory.CreateClient();
        var dto = CredencialInvalida();

        // Act & Assert — exatamente 5 (PermitLimit) devem ser permitidas
        for (var i = 1; i <= 5; i++)
        {
            var resp = await client.PostAsJsonAsync(RotaLogin, dto);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }
}
