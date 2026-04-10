using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Models;

namespace TccManager.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Usuario> Usuarios { get; set; }
    public DbSet<Tcc> Tccs { get; set; }
    public DbSet<Entrega> Entregas { get; set; }
    public DbSet<Acompanhamento> Acompanhamentos { get; set; }
    public DbSet<Banca> Banca { get; set; }
    public DbSet<BancaAvaliador> BancaAvaliadores { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Usuario>().ToTable("usuarios");

        modelBuilder.Entity<BancaAvaliador>()
            .HasOne(ba => ba.Professor)
            .WithMany()
            .HasForeignKey(ba => ba.ProfessorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
