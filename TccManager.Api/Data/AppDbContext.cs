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

        modelBuilder.Entity<Usuario>(entity =>
        {
            entity.ToTable("usuarios");

            // Reforça no banco a invariante de e-mail único por usuário — ver
            // UsuarioController.CreateUsuario/UpdateUsuario, que já validam isso na aplicação.
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Entrega>(entity =>
        {
            // Reforça no banco a invariante "no máximo 1 entrega FINAL NÃO REJEITADA por
            // TCC" (RN03 + issue #81), como backstop atômico ao pre-check de aplicação em
            // TccController.EnviarEntrega — que sozinho não impede duas requisições
            // concorrentes de passarem no pre-check e gerarem duas entregas FINAL "ativas".
            // TipoEntrega.Final == 1, StatusEntrega.Rejeitada == 2 (sem HasConversion, enums
            // persistidos como int).
            //
            // Issue #81 (D2): o filtro passou de "[Tipo] = 1" para "[Tipo] = 1 AND [Status] <> 2"
            // — o mecanismo de veredito por entrega permite que uma entrega Final rejeitada
            // saia do índice para o aluno poder enviar uma nova Final no lugar (ver
            // docs/dados/2026-08-30-reprovacao-durante-orientacao.md, seção 3). `<>` (e não
            // `IN (0, 1)`) foi validado empiricamente contra SQL Server real e é mais robusto
            // a um futuro valor de enum "ativo" adicional. O predicado precisa ser mantido em
            // paridade com o pre-check de TccController.EnviarEntrega (D6) — essa igualdade é
            // a invariante central da issue.
            //
            // Substitui o índice não-único de convenção (FK em TccId): o EF Core não
            // suporta dois índices distintos sobre o mesmo conjunto de propriedades no
            // Fluent API (a segunda definição sempre sobrescreve a primeira no modelo,
            // mesmo com HasDatabaseName diferente) — trade-off aceito, não bloqueante,
            // registrado em docs/implementacao.
            entity.HasIndex(e => e.TccId)
                .IsUnique()
                .HasFilter("[Tipo] = 1 AND [Status] <> 2")
                .HasDatabaseName("UX_Entregas_TccId_Final");
        });

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
