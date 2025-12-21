using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Models;

namespace TccManager.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Usuario> Usuarios { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Usuario>().ToTable("usuarios");

        // Ignora a classe ClientOptions do Supabase para evitar erro de mapeamento no EF Core
        modelBuilder.Ignore<Supabase.Postgrest.ClientOptions>();
    }
}
