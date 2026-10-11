using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components;
using TccManager.Client.Providers;

namespace TccManager.Client.Services;

public class SessionEndedHandler : ISessionEndedHandler
{
    private readonly ILocalStorageService _localStorage;
    private readonly NavigationManager _navigation;
    private readonly CustomAuthStateProvider _authStateProvider;

    public SessionEndedHandler(
        ILocalStorageService localStorage,
        NavigationManager navigation,
        CustomAuthStateProvider authStateProvider)
    {
        _localStorage = localStorage;
        _navigation = navigation;
        _authStateProvider = authStateProvider;
    }

    public async Task EncerrarSessaoAsync()
    {
        await _localStorage.RemoveItemAsync("authToken");
        // D9 (issue #171): "refreshToken" é a chave legada do localStorage — desde a migração
        // para o cookie HttpOnly, o Client nunca mais escreve nem lê o valor dela, só remove.
        await _localStorage.RemoveItemAsync("refreshToken");

        _authStateProvider.NotifyUserLogout();

        _navigation.NavigateTo("/login?expirado=1");
    }
}
