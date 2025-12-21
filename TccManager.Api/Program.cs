using Microsoft.EntityFrameworkCore;
using Supabase;
using TccManager.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configuração do Entity Framework (Banco de Dados)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

var supabaseUrl = builder.Configuration["Supabase:Url"] ?? throw new InvalidOperationException("Supabase URL não foi configurada");
var supabaseKey = builder.Configuration["Supabase:Key"] ?? throw new InvalidOperationException("Supabase Key não foi configurada");

var options = new SupabaseOptions
{
    AutoConnectRealtime = false,
    AutoRefreshToken = true
};

var supabase = new Supabase.Client(supabaseUrl, supabaseKey, options);
await supabase.InitializeAsync();

builder.Services.AddSingleton(supabase);

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

// Em desenvolvimento local sem certificado, comente a linha abaixo para evitar o aviso de porta HTTPS
// app.UseHttpsRedirection();
app.MapControllers();

app.Run();