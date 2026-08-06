namespace TccManager.Client.Shared.Navegacao;

/// <summary>
/// Um atalho de navegação (rótulo + ícone Material Symbols + rota) espelhando um item já
/// existente em <see cref="TccManager.Client.Layout.NavMenu"/> para um perfil (role) específico.
/// </summary>
public record AtalhoPerfil(string Rotulo, string Icone, string Rota);
