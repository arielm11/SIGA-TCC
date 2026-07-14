using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using TccManager.Api.Binders;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Filters;
using TccManager.Api.Middleware;
using TccManager.Api.ModelBinding;
using TccManager.Api.Services;
using TccManager.Api.Services.Auth;
using TccManager.Api.Services.Storage;
using System.Text;
using Serilog;

LoggingSetup.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.ConfigureLogging();

    builder.Services.AddControllers(options =>
    {
        options.ModelBinderProviders.Insert(0, new InvariantDecimalModelBinderProvider());
        options.ModelBinderProviders.Insert(0, new PaginacaoQueryModelBinderProvider());
        options.Filters.Add<FluentValidationActionFilter>();
    })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // Configuração do Entity Framework (Banco de Dados)
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

    var jwtKey = Encoding.ASCII.GetBytes(builder.Configuration["Jwt:Key"]!);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
            ValidateIssuer = false,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = false,
            ValidAudience = builder.Configuration["Jwt:Audience"]
        };
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowBlazorClient",
            policy =>
            {
                policy.WithOrigins("https://localhost:7249", "http://localhost:5075")
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
    });

    builder.Services.ConfigureRateLimiting(builder.Configuration);

    builder.Services.AddSingleton<ISanitizerService, HtmlSanitizerService>();
    builder.Services.AddSingleton<IStorageService, LocalStorageService>();

    builder.Services.AddScoped<ITokenService, TokenService>();
    builder.Services.AddScoped<IAuthTokenService, AuthTokenService>();

    builder.Services.AddEmailNotifications(builder.Configuration);
    builder.Services.AddAtaPdf(builder.Configuration);

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
            options.RoutePrefix = string.Empty;
        });
    }

    app.UseMiddleware<CorrelationIdMiddleware>();

    // MessageTemplate customizado para não expor o token bruto do rascunho no path
    // quando essa rota lançar exceção (nível Error, acima do MinimumLevel.Default).
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPathRedacted} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            var path = httpContext.Request.Path.Value ?? string.Empty;
            var redigido = path.StartsWith("/api/rascunho-ata/", StringComparison.OrdinalIgnoreCase)
                ? "/api/rascunho-ata/[REDACTED]"
                : path;
            diagnosticContext.Set("RequestPathRedacted", redigido);
        };
    });

    app.UseHttpsRedirection();

    app.UseStaticFiles();

    app.UseCors("AllowBlazorClient");

    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Aplicação encerrada inesperadamente durante a inicialização");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
