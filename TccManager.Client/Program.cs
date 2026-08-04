using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TccManager.Client;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using TccManager.Client.Providers;
using TccManager.Client.Handlers;
using TccManager.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? throw new Exception("URL da API não configurada!");

builder.Services.AddBlazoredLocalStorage();

builder.Services.AddAuthorizationCore();

builder.Services.AddScoped<CustomAuthStateProvider>();

builder.Services.AddScoped<AuthenticationStateProvider>(provider =>
    provider.GetRequiredService<CustomAuthStateProvider>());

builder.Services.AddScoped<ISessionEndedHandler, SessionEndedHandler>();
builder.Services.AddScoped<ITokenRefreshCoordinator, TokenRefreshCoordinator>();

// Registra DialogService, NotificationService, TooltipService e ContextMenuService de uma vez
// (helper disponível no pacote Radzen.Blazor referenciado).
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<TemaService>();

builder.Services.AddTransient<AuthTokenHandler>();

// Cliente "cru", sem o AuthTokenHandler — usado exclusivamente pelo handler/coordenador
// para chamar /api/auth/refresh e /api/auth/logout, evitando recursão (§6.1 da
// arquitetura: um 401 vindo do próprio /refresh nunca reentra no interceptor).
builder.Services.AddHttpClient("AuthRaw", c => c.BaseAddress = new Uri(apiBaseUrl));

// Cliente "Api", com o AuthTokenHandler no pipeline.
builder.Services.AddHttpClient("Api", c => c.BaseAddress = new Uri(apiBaseUrl))
    .AddHttpMessageHandler<AuthTokenHandler>();

// Mantém "@inject HttpClient" funcionando em todas as páginas, agora já com o handler
// no pipeline por trás (nenhuma página precisa ser alterada).
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("Api"));

var host = builder.Build();

// Sincroniza o estado em memória do TemaService com a preferência já aplicada visualmente pelo
// script inline de wwwroot/index.html (anti-flash), antes de qualquer componente renderizar.
await host.Services.GetRequiredService<TemaService>().InicializarAsync();

await host.RunAsync();
