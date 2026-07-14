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
    public DbSet<MembroExterno> MembrosExternos { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<RascunhoAtaToken> RascunhoAtaTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Usuario>().ToTable("usuarios");

        modelBuilder.Entity<BancaAvaliador>()
            .HasOne(ba => ba.Professor)
            .WithMany()
            .HasForeignKey(ba => ba.ProfessorId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");

            entity.Property(rt => rt.TokenHash)
                .HasColumnType("char(64)")
                .IsRequired();

            entity.Property(rt => rt.ReplacedByTokenHash)
                .HasColumnType("char(64)");

            entity.HasIndex(rt => rt.TokenHash).IsUnique();
            entity.HasIndex(rt => rt.UsuarioId);

            entity.HasOne(rt => rt.Usuario)
                .WithMany()
                .HasForeignKey(rt => rt.UsuarioId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RascunhoAtaToken>(entity =>
        {
            entity.ToTable("rascunho_ata_tokens");

            entity.Property(t => t.TokenHash)
                .HasColumnType("char(64)")
                .IsRequired();

            entity.HasIndex(t => t.TokenHash).IsUnique();

            // Reforça no banco a invariante "no máximo 1 token ativo por par" — ver
            // docs/dados/2026-07-13-pdf-ata-rascunho-etapa2.md, seção 3.1.
            entity.HasIndex(t => new { t.BancaId, t.MembroExternoId })
                .IsUnique()
                .HasFilter("[RevokedAtUtc] IS NULL")
                .HasDatabaseName("UX_rascunho_ata_tokens_Banca_Membro_Ativo");

            entity.HasOne(t => t.Banca)
                .WithMany()
                .HasForeignKey(t => t.BancaId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(t => t.MembroExterno)
                .WithMany()
                .HasForeignKey(t => t.MembroExternoId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
