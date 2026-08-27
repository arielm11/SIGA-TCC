using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using TccManager.Api.Binders;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Api.Filters;
using TccManager.Api.Logging;
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

    // Middleware de exceção global (issue #71) — ver GlobalExceptionHandler para o motivo de
    // nunca devolver detalhe de exceção no corpo, em nenhum ambiente.
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    // Log estruturado de falha de validação (issue #71): envolve o factory PADRÃO do
    // framework (usado tanto pelo filtro automático de ModelState do [ApiController] para
    // DataAnnotations quanto por FluentValidationActionFilter, que delega para o mesmo
    // IOptions<ApiBehaviorOptions>.Value.InvalidModelStateResponseFactory) — loga só os
    // NOMES dos campos que falharam, nunca os valores submetidos, para dar visibilidade de
    // abuso/fuzzing sem virar mais um lugar que registra dado de usuário.
    //
    // PostConfigure (não Configure): Configure<T> compõe na ordem de REGISTRO, então
    // capturar o factory padrão do framework dependeria de este bloco vir depois de
    // AddControllers(...) no arquivo — inverter as duas silenciosamente desativaria o log,
    // sem erro nenhum (achado A06-1). PostConfigure roda sempre por último, independente de
    // ordem de registro, e o guard abaixo falha o startup em vez de degradar em silêncio.
    builder.Services.PostConfigure<ApiBehaviorOptions>(options =>
    {
        var factoryPadrao = options.InvalidModelStateResponseFactory
            ?? throw new InvalidOperationException(
                "ApiBehaviorOptions.InvalidModelStateResponseFactory padrão não estava configurado " +
                "quando o wrapper de log de validação foi aplicado.");

        options.InvalidModelStateResponseFactory = context =>
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("TccManager.Api.ValidationFailures");

            var campos = context.ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .Select(kv => kv.Key)
                .ToArray();

            // Ator (id de usuário autenticado, ou IP para requisição anônima — ex.: login,
            // rascunho público): sem isso o log responde "qual campo falhou" mas não "quem
            // está fuzzando", que é o objetivo declarado da issue (achado A09-3).
            var usuarioId = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var ator = usuarioId ?? $"anon:{context.HttpContext.Connection.RemoteIpAddress}";

            // "campos" passado como array, não concatenado em string: preserva a estrutura
            // (Serilog escapa cada elemento) em vez de virar uma única string opaca — e evita
            // que uma chave de ModelState hostil (ex.: derivada de JSON path malformado, com
            // CRLF) forje uma linha extra no sink de arquivo (achado A05-1).
            logger.LogWarning(
                "Falha de validação em {Method} {RequestPath}. Ator: {Ator}, Campos: {Campos}",
                context.HttpContext.Request.Method,
                RequestPathRedactor.Redigir(context.HttpContext.Request.Path.Value),
                ator,
                campos);

            return factoryPadrao(context);
        };
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // Configuração do Entity Framework (Banco de Dados)
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

    var jwtKey = Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!);

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
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            // Tolerância pequena, apenas para diferença de relógio entre servidores —
            // não deve estender de forma relevante a vida útil do access token (15 min).
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

    // Origens permitidas configuráveis via "Cors:AllowedOrigins" (appsettings.json), com
    // fallback para os dois valores de dev atuais caso a seção não exista — mantém o
    // ambiente de dev funcionando sem exigir edição de appsettings.Development.json.
    // O appsettings.json versionado deliberadamente NÃO fixa esses dois valores na seção
    // "Cors" (fica só {}): arrays de configuração no .NET fazem merge por índice entre
    // camadas (appsettings.json -> appsettings.{Environment}.json -> env vars), não
    // substituição — um appsettings.Production.json sobrescrevendo só o índice 0 deixaria o
    // índice 1 (localhost:5075) vazando para produção. Achado do security-reviewer,
    // 2026-08-17 (docs/seguranca/2026-08-17-fix-config-hardening.md).
    var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
    if (corsAllowedOrigins is null || corsAllowedOrigins.Length == 0)
    {
        corsAllowedOrigins = ["https://localhost:7249", "http://localhost:5075"];
    }

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowBlazorClient",
            policy =>
            {
                policy.WithOrigins(corsAllowedOrigins)
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

    // App:PublicApiBaseUrl / App:ClientBaseUrl não têm valor padrão sensato e, se vazios,
    // não derrubam o resto do sistema (login, propostas, entregas, bancas seguem
    // funcionando) — só quebram o link montado no corpo do e-mail de acesso ao rascunho
    // da ata (TccNotificationService), que sempre engole a exceção internamente
    // (try/catch + log, nunca propaga). Por isso: warning visível no startup, não fail-fast.
    var publicApiBaseUrl = app.Configuration["App:PublicApiBaseUrl"];
    var clientBaseUrl = app.Configuration["App:ClientBaseUrl"];
    if (string.IsNullOrWhiteSpace(publicApiBaseUrl))
    {
        app.Logger.LogWarning(
            "App:PublicApiBaseUrl não configurada — o link do rascunho da ata enviado por " +
            "e-mail ao avaliador externo será montado com URL inválida.");
    }
    if (string.IsNullOrWhiteSpace(clientBaseUrl))
    {
        app.Logger.LogWarning(
            "App:ClientBaseUrl não configurada — o link de atalho para login enviado por " +
            "e-mail aos avaliadores internos será montado com URL inválida.");
    }

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

    // Depois do CorrelationIdMiddleware (para o CorrelationId já estar no LogContext quando
    // uma exceção é logada) e antes de tudo que pode lançar (roteamento, auth, controllers).
    app.UseExceptionHandler();

    // MessageTemplate customizado para não expor o token bruto do rascunho no path
    // quando essa rota lançar exceção (nível Error, acima do MinimumLevel.Default).
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPathRedacted} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestPathRedacted", RequestPathRedactor.Redigir(httpContext.Request.Path.Value));
        };
    });

    app.UseHttpsRedirection();

    // Sem UseStaticFiles(): wwwroot/uploads (entregas/atas/propostas) nunca deve ser servido
    // sem autenticação/autorização. O único uso de wwwroot hoje é esse diretório de uploads,
    // acessado exclusivamente via LocalStorageService (I/O direto em disco, não HTTP) e
    // exposto ao cliente só por endpoints autenticados (ex.: TccController.DownloadEntrega).
    // Ver achado A01-1, docs/seguranca/2026-08-18-fix-upload-storage-hardening.md.

    app.UseCors("AllowBlazorClient");

    // UseAuthentication antes de UseRateLimiter: a política "geracao-pdf" precisa de
    // HttpContext.User já populado para particionar por usuário autenticado em vez de IP
    // (achado A02-2, docs/seguranca/2026-08-18-fix-upload-storage-hardening.md — partição
    // por IP, atrás de proxy sem UseForwardedHeaders, colapsava a cota de toda a rede/campus
    // num único bucket, consumível inclusive por requisição anônima). As políticas
    // pré-autenticação (login/refresh/rascunho-publico) continuam particionadas por IP
    // explicitamente dentro de si mesmas em RateLimitingSetup — não dependem de User e não
    // são afetadas pela troca de ordem.
    app.UseAuthentication();

    app.UseRateLimiter();

    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Aplicação encerrada inesperadamente durante a inicialização");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
