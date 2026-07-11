using TccManager.Api.Services.Email;
using TccManager.Api.Services.Notifications;

namespace TccManager.Api.Configuration;

/// <summary>
/// Registro em DI do subsistema de notificações por e-mail (N1). Segue o mesmo padrão de
/// extensão estática já usado em RateLimitingSetup. Config lida da seção "Email" (host,
/// porta, usuário, senha e remetente via User Secrets em desenvolvimento — ver appsettings.json).
/// </summary>
public static class EmailSetup
{
    public static IServiceCollection AddEmailNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection("Email"));

        services.AddSingleton<IEmailQueue, ChannelEmailQueue>();
        services.AddSingleton<IEmailService, MailKitEmailService>();
        services.AddSingleton<IEmailTemplateRenderer, FileEmailTemplateRenderer>();
        services.AddHostedService<EmailBackgroundService>();

        services.AddScoped<ITccNotificationService, TccNotificationService>();

        return services;
    }
}
