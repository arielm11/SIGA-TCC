using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;

namespace TccManager.Client.Providers;
public class CustomAuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;
    private readonly HttpClient _http;

    public CustomAuthStateProvider(ILocalStorageService localStorage, HttpClient http)
    {
        _localStorage = localStorage;
        _http = http;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var anonymousState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        var token = await _localStorage.GetItemAsStringAsync("authToken");

        if (string.IsNullOrWhiteSpace(token))
            return anonymousState;

        token = token.Replace("\"", "");

        var claims = ParseClaimsFromJwt(token);

        var expClaim = claims.FirstOrDefault(c => c.Type == "exp");
        if (expClaim != null && long.TryParse(expClaim.Value, out long expTime))
        {
            var dataExpiracao = DateTimeOffset.FromUnixTimeSeconds(expTime).UtcDateTime;

            if (dataExpiracao <= DateTime.UtcNow)
            {
                await _localStorage.RemoveItemAsync("authToken");
                _http.DefaultRequestHeaders.Authorization = null;
                return anonymousState;
            }
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var identity = new ClaimsIdentity(claims, "jwt"); 
        var usuario = new ClaimsPrincipal(identity);

        return new AuthenticationState(usuario);
    }

    public void NotifyUserAuthentication(string token) 
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var claims = ParseClaimsFromJwt(token);
        var identity = new ClaimsIdentity(claims, "jwt");
        var usuario = new ClaimsPrincipal(identity);

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(usuario)));
    }

    public void NotifyUserLogout()
    {
        _http.DefaultRequestHeaders.Authorization = null;

        var anonymousUser = new ClaimsPrincipal(new ClaimsIdentity());

        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(anonymousUser)));
    }

    private IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var claims = new List<Claim>();
        var payload = jwt.Split('.')[1];
        var jsonBytes = ParseBase64WithoutPadding(payload);
        var keyValuePairs = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);

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

    private byte[] ParseBase64WithoutPadding(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
