using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;

namespace TccManager.Api.Data;

/// <summary>
/// Issue #88 (D12): única definição de "Admin ativo", compartilhada entre a guarda existente
/// de UsuarioController (<c>EhUnicoAdminAtivoAsync</c>, <c>&lt;= 1</c>) e a checagem nova do
/// bootstrap (<c>AdminBootstrapSetup</c>, <c>== 0</c>). Mesma query, mesmo comportamento —
/// evita duas definições divergentes do mesmo predicado.
/// </summary>
public static class UsuarioQueries
{
    public static Task<int> ContarAdminsAtivosAsync(this AppDbContext ctx) =>
        ctx.Usuarios.CountAsync(u => u.Tipo == TipoUsuario.Admin && u.Ativo);

    /// <summary>
    /// Issue #112 (arquitetura D4 / modelagem seção 6): única definição de "professor listável
    /// com carga atual", extraída de CoordenadorController.GetProfessores para ser reaproveitada
    /// também pelo endpoint novo do Aluno (TccController.GetProfessores) — evita duas definições
    /// divergentes do mesmo predicado (mesmo racional de ContarAdminsAtivosAsync/#88).
    /// Não materializado (sem OrderBy/ToListAsync/ToPagedResultAsync): cada chamador aplica sua
    /// própria ordenação e paginação.
    /// </summary>
    public static IQueryable<ProfessorResumoDto> ProfessoresAtivosComCarga(this AppDbContext ctx) =>
        ctx.Usuarios
            .Where(u => u.Tipo == TipoUsuario.Professor && u.Ativo)
            .Select(u => new ProfessorResumoDto
            {
                Id = u.Id,
                Nome = u.Nome,
                LimiteOrientandos = u.LimiteOrientandos,
                AceitandoOrientandos = u.AceitandoOrientandos,
                CargaAtual = ctx.Tccs.Count(t => t.OrientadorId == u.Id && (t.Status == StatusTcc.Aprovado || t.Status == StatusTcc.EmAndamento))
            });
}
