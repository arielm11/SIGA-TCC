using TccManager.Client.Shared.Navegacao;
using Xunit;

namespace TccManager.Tests.Client.Shared.Navegacao;

/// <summary>
/// Testes de <see cref="AtalhosPerfil"/>. O conteúdo é uma tabela estática (role → atalhos),
/// então o valor real aqui é: (1) travar a paridade de rotas documentada (RF-03 — os atalhos da
/// Home/NavMenu devem bater com as rotas reais das páginas) e (2) invariantes estruturais que a
/// migração introduziu como decisão explícita (rotas relativas, sem "/" inicial). Não há lógica
/// de branch aqui — o mapeamento role→lista vive em Home.razor/NavMenu.razor (não testável sem
/// bUnit); ver docs/testes.
/// </summary>
public class AtalhosPerfilTests
{
    public static IEnumerable<object[]> TodasAsListas =>
    [
        [AtalhosPerfil.Admin],
        [AtalhosPerfil.Aluno],
        [AtalhosPerfil.Professor],
        [AtalhosPerfil.Coordenador],
    ];

    // ── Paridade de rotas (RF-03) ─────────────────────────────────────

    [Fact]
    public void Admin_TemAtalhoDeUsuarios()
    {
        Assert.Equal(["usuarios"], AtalhosPerfil.Admin.Select(a => a.Rota));
    }

    [Fact]
    public void Aluno_TemAtalhoDeMeuTcc()
    {
        Assert.Equal(["meu-tcc"], AtalhosPerfil.Aluno.Select(a => a.Rota));
    }

    [Fact]
    public void Professor_TemDashboardEConvitesDeBanca()
    {
        Assert.Equal(
            ["professor/dashboard", "avaliador/convites"],
            AtalhosPerfil.Professor.Select(a => a.Rota));
    }

    [Fact]
    public void Coordenador_TemAsCincoRotasDoPerfil()
    {
        Assert.Equal(
            [
                "coordenador/dashboard",
                "coordenador/professores",
                "coordenador/agendar-banca",
                "coordenador/registrar-resultado",
                "coordenador/bancas-concluidas"
            ],
            AtalhosPerfil.Coordenador.Select(a => a.Rota));
    }

    // ── Invariantes estruturais ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(TodasAsListas))]
    public void CadaLista_NaoEhVazia(IReadOnlyList<AtalhoPerfil> atalhos)
    {
        Assert.NotEmpty(atalhos);
    }

    [Theory]
    [MemberData(nameof(TodasAsListas))]
    public void CadaAtalho_TemRotuloIconeERotaPreenchidos(IReadOnlyList<AtalhoPerfil> atalhos)
    {
        Assert.All(atalhos, atalho =>
        {
            Assert.False(string.IsNullOrWhiteSpace(atalho.Rotulo));
            Assert.False(string.IsNullOrWhiteSpace(atalho.Icone));
            Assert.False(string.IsNullOrWhiteSpace(atalho.Rota));
        });
    }

    [Theory]
    [MemberData(nameof(TodasAsListas))]
    public void CadaRota_EhRelativaSemBarraInicial(IReadOnlyList<AtalhoPerfil> atalhos)
    {
        // Decisão de padronização registrada na implementação: todos os Path usam string
        // relativa sem "/" inicial (antes o NavMenu misturava href="usuarios" e href="/coord...").
        Assert.All(atalhos, atalho => Assert.False(atalho.Rota.StartsWith('/')));
    }

    [Theory]
    [MemberData(nameof(TodasAsListas))]
    public void CadaLista_NaoTemRotaDuplicada(IReadOnlyList<AtalhoPerfil> atalhos)
    {
        var rotas = atalhos.Select(a => a.Rota).ToList();
        Assert.Equal(rotas.Count, rotas.Distinct().Count());
    }
}
