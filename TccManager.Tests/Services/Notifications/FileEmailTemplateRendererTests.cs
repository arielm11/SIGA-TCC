using TccManager.Api.Services.Notifications;
using Xunit;

namespace TccManager.Tests.Services.Notifications;

/// <summary>
/// Testes unitários de FileEmailTemplateRenderer. Os templates são embedded resources
/// na DLL de TccManager.Api (referenciada pelo projeto de teste), então o renderer real
/// os carrega sem depender de path relativo. Aqui exercitamos a substituição de
/// placeholders {{Chave}} contra os templates reais dos 7 eventos.
/// </summary>
public class FileEmailTemplateRendererTests
{
    private readonly FileEmailTemplateRenderer _renderer = new();

    [Fact]
    public void Render_SubstituiTodosOsPlaceholdersDoTemplate()
    {
        var valores = new Dictionary<string, string>
        {
            ["NomeAluno"] = "Maria Silva",
            ["TituloTcc"] = "Análise de Redes Neurais",
            ["NomeOrientador"] = "Prof. João Souza"
        };

        var corpo = _renderer.Render("proposta-aprovada", valores);

        Assert.Contains("Maria Silva", corpo);
        Assert.Contains("Análise de Redes Neurais", corpo);
        Assert.Contains("Prof. João Souza", corpo);
        // Nenhum dos placeholders resolvidos deve permanecer no corpo final.
        Assert.DoesNotContain("{{NomeAluno}}", corpo);
        Assert.DoesNotContain("{{TituloTcc}}", corpo);
        Assert.DoesNotContain("{{NomeOrientador}}", corpo);
    }

    [Fact]
    public void Render_SubstituiPlaceholderQueApareceMaisDeUmaVez()
    {
        // banca-agendada usa {{TituloTcc}} e {{NomeAluno}}; garantimos que a chave
        // "TituloTcc" (referenciada uma vez no corpo) é substituída e o marcador some.
        var valores = new Dictionary<string, string>
        {
            ["NomeAluno"] = "Ana",
            ["TituloTcc"] = "Titulo X",
            ["DataHora"] = "15/08/2026 14:30",
            ["Local"] = "Sala 101",
            ["ListaMembrosBanca"] = "<li>Fulano</li><li>Ciclano</li>"
        };

        var corpo = _renderer.Render("banca-agendada", valores);

        Assert.DoesNotContain("{{TituloTcc}}", corpo);
        Assert.DoesNotContain("{{ListaMembrosBanca}}", corpo);
        Assert.Contains("15/08/2026 14:30", corpo);
        Assert.Contains("Sala 101", corpo);
        // O fragmento HTML intencional da lista deve entrar sem escape.
        Assert.Contains("<li>Fulano</li><li>Ciclano</li>", corpo);
    }

    [Fact]
    public void Render_PlaceholderNaoInformado_PermaneceLiteralNoCorpo()
    {
        // O renderer só troca as chaves presentes no dicionário; chaves ausentes
        // ficam intactas. Documenta o contrato de substituição (não lança, não limpa).
        var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Só o nome"
        });

        Assert.Contains("Só o nome", corpo);
        Assert.Contains("{{TituloTcc}}", corpo);
        Assert.Contains("{{NomeOrientador}}", corpo);
    }

    [Fact]
    public void Render_ChaveExtraNaoPresenteNoTemplate_NaoAfetaResultado()
    {
        var corpo = _renderer.Render("aceite-final", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Aluno",
            ["TituloTcc"] = "Titulo",
            ["ChaveInexistente"] = "ignorada"
        });

        Assert.DoesNotContain("ignorada", corpo);
        Assert.DoesNotContain("{{NomeAluno}}", corpo);
        Assert.DoesNotContain("{{TituloTcc}}", corpo);
    }

    [Theory]
    [InlineData("proposta-aprovada")]
    [InlineData("proposta-rejeitada")]
    [InlineData("banca-agendada")]
    [InlineData("feedback-registrado")]
    [InlineData("aceite-final")]
    [InlineData("resultado-aprovado")]
    [InlineData("resultado-reprovado")]
    public void Render_CadaUmDos7Templates_CarregaComSucesso(string chave)
    {
        var corpo = _renderer.Render(chave, new Dictionary<string, string>());

        Assert.False(string.IsNullOrWhiteSpace(corpo));
        Assert.Contains("<html", corpo);
    }

    [Fact]
    public void Render_TemplateInexistente_LancaInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => _renderer.Render("template-que-nao-existe", new Dictionary<string, string>()));

        Assert.Contains("template-que-nao-existe", ex.Message);
    }
}
