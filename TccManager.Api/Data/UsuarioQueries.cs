using Microsoft.EntityFrameworkCore;
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
}
