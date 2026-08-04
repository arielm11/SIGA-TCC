namespace TccManager.Client.Shared.Navegacao;

/// <summary>
/// Fonte única de verdade dos atalhos de navegação por role (Admin/Aluno/Professor/Coordenador),
/// consumida por <see cref="TccManager.Client.Layout.NavMenu"/> e por
/// <c>TccManager.Client.Pages.Home</c>, para as duas telas não divergirem sobre quais rotas
/// existem para cada perfil. Rótulos, ícones e rotas espelham exatamente o que já existia em
/// NavMenu.razor antes da migração para Radzen (paridade de navegação — RF-03).
/// </summary>
public static class AtalhosPerfil
{
    public static readonly IReadOnlyList<AtalhoPerfil> Admin =
    [
        new("Usuários", "group", "usuarios")
    ];

    public static readonly IReadOnlyList<AtalhoPerfil> Aluno =
    [
        new("Meu TCC", "book", "meu-tcc")
    ];

    public static readonly IReadOnlyList<AtalhoPerfil> Professor =
    [
        new("Dashboard", "dashboard", "professor/dashboard"),
        new("Convites de Banca", "mail", "avaliador/convites")
    ];

    public static readonly IReadOnlyList<AtalhoPerfil> Coordenador =
    [
        new("Dashboard", "dashboard", "coordenador/dashboard"),
        new("Gestão de Professores", "group", "coordenador/professores"),
        new("Agendar Banca", "event", "coordenador/agendar-banca"),
        new("Registrar Resultado", "workspace_premium", "coordenador/registrar-resultado"),
        new("Bancas Concluídas", "fact_check", "coordenador/bancas-concluidas")
    ];
}
