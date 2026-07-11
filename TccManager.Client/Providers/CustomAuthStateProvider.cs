using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Client.Services;

namespace TccManager.Client.Providers;

public class CustomAuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;
    private readonly IServiceProvider _serviceProvider;

    // ITokenRefreshCoordinator é resolvido sob demanda (via IServiceProvider) em vez de
    // injetado diretamente no construtor: o coordenador, por sua vez, depende deste
    // provider (para chamar NotifyUserAuthentication após renovar — §6.3 da
    // arquitetura), então a injeção direta nos dois sentidos formaria um ciclo de DI.
    public CustomAuthStateProvider(ILocalStorageService localStorage, IServiceProvider serviceProvider)
    {
        _localStorage = localStorage;
        _serviceProvider = serviceProvider;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var anonymousState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        var token = await _localStorage.GetItemAsStringAsync("authToken");

        if (string.IsNullOrWhiteSpace(token))
            return anonymousState;

        token = token.Replace("\"", string.Empty);

        if (EstaExpirado(token))
        {
            // P-C1: o access token expirou, mas pode existir um refresh token ainda
            // válido — tenta renovar (reaproveitando o mesmo coordenador single-flight
            // usado pelo handler de 401) antes de declarar a sessão anônima, evitando
            // deslogar prematuramente uma sessão ainda renovável apenas por navegação.
            var refreshCoordinator = _serviceProvider.GetRequiredService<ITokenRefreshCoordinator>();
            var refreshOk = await refreshCoordinator.EnsureRefreshedAsync(token);

            if (!refreshOk)
                return anonymousState;

            var tokenRenovado = await _localStorage.GetItemAsStringAsync("authToken");
            if (string.IsNullOrWhiteSpace(tokenRenovado))
                return anonymousState;

            token = tokenRenovado.Replace("\"", string.Empty);
        }

        var claims = ParseClaimsFromJwt(token);
        var identity = new ClaimsIdentity(claims, "jwt");
        var usuario = new ClaimsPrincipal(identity);

        return new AuthenticationState(usuario);
    }

    public void NotifyUserAuthentication(string token)
    {
        var claims = ParseClaimsFromJwt(token);
        var identity = new ClaimsIdentity(claims, "jwt");
        var usuario = new ClaimsPrincipal(identity);

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(usuario)));
    }

    public void NotifyUserLogout()
    {
        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(anonymousUser)));
    }

    private static bool EstaExpirado(string token)
    {
        var claims = ParseClaimsFromJwt(token);
        var expClaim = claims.FirstOrDefault(c => c.Type == "exp");

        if (expClaim == null || !long.TryParse(expClaim.Value, out var expTime))
            return false;

        var dataExpiracao = DateTimeOffset.FromUnixTimeSeconds(expTime).UtcDateTime;
        return dataExpiracao <= DateTime.UtcNow;
    }

    private static IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var claims = new List<Claim>();

        Dictionary<string, object>? keyValuePairs;
        try
        {
            var payload = jwt.Split('.')[1];
            var jsonBytes = ParseBase64WithoutPadding(payload);
            keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException or JsonException)
        {
            // Token malformado em localStorage (ex.: corrompido manualmente): trata como
            // anônimo em vez de propagar exceção para GetAuthenticationStateAsync.
            return claims;
        }

        if (keyValuePairs != null)
        {
            foreach (var kvp in keyValuePairs)
            {
                var value = kvp.Value.ToString() ?? "";
                var claimType = kvp.Key;

                if (claimType == "role" || claimType.Contains("/claims/role"))
                {
                    claimType = ClaimTypes.Role;
                }
                else if (claimType == "unique_name" || claimType.Contains("/claims/unique_name"))
                {
                    claimType = ClaimTypes.Name;
                }
                else if (claimType == "nameid" || claimType.Contains("/claims/nameid"))
                {
                    claimType = ClaimTypes.NameIdentifier;
                }

                if (value.Trim().StartsWith("["))
                {
                    try
                    {
                        var parsedValues = JsonSerializer.Deserialize<string[]>(value);
                        if (parsedValues != null)
                        {
                            foreach (var parsedValue in parsedValues)
                            {
                                claims.Add(new Claim(claimType, parsedValue));
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        claims.Add(new Claim(claimType, value));
                        Console.WriteLine($"Erro ao desserializar o valor do claim '{claimType}': {value}");
                    }
                }
                else
                {
                    claims.Add(new Claim(claimType, value));
                }
            }
        }

        return claims;
    }

    private static byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
