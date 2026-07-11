using TccManager.Api.Services;
using Xunit;

namespace TccManager.Tests.Services;

public class HtmlSanitizerServiceTests
{
    private readonly HtmlSanitizerService _sut = new();

    [Fact]
    public void Sanitizar_ComTagScript_RemoveTagsDeixandoTextoInerte()
    {
        var resultado = _sut.Sanitizar("<script>alert(1)</script>");

        // Política "remover todo HTML": o texto interno vira texto plano (RF2),
        // mas nenhuma tag/marcação executável pode sobrar na saída.
        Assert.NotNull(resultado);
        Assert.DoesNotContain("<script", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", resultado);
        Assert.DoesNotContain(">", resultado);
    }

    [Fact]
    public void Sanitizar_ComImgOnError_RemoveTagEAtributoDeEvento()
    {
        var resultado = _sut.Sanitizar("<img src=x onerror=\"alert(1)\">");

        Assert.NotNull(resultado);
        Assert.DoesNotContain("<img", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", resultado);
        Assert.DoesNotContain(">", resultado);
    }

    [Fact]
    public void Sanitizar_ComTagFormatacao_RemoveTagPreservandoTexto()
    {
        var resultado = _sut.Sanitizar("<b>texto</b>");

        // KeepChildNodes = true: a tag some, o texto interno permanece.
        Assert.Equal("texto", resultado);
    }

    [Fact]
    public void Sanitizar_ComEntradaNula_RetornaNulo()
    {
        var resultado = _sut.Sanitizar(null);

        Assert.Null(resultado);
    }

    [Fact]
    public void Sanitizar_ComEntradaVazia_RetornaStringVazia()
    {
        var resultado = _sut.Sanitizar(string.Empty);

        Assert.Equal(string.Empty, resultado);
    }

    [Theory]
    [InlineData("Texto simples sem nenhuma marcação")]
    [InlineData("Sistema de Gestão de TCCs")]
    [InlineData("Análise de desempenho: estudo de caso")]
    public void Sanitizar_ComTextoSemHtml_RetornaExatamenteIgual(string entrada)
    {
        var resultado = _sut.Sanitizar(entrada);

        Assert.Equal(entrada, resultado);
    }

    [Fact]
    public void Sanitizar_ComCaracteresEspeciais_MantemComoEntidadesHtmlSeguras()
    {
        // Caracteres especiais digitados literalmente pelo usuário (não são tags) ficam
        // preservados como entidade HTML (ex.: "&lt;"), nunca decodificados de volta para
        // "<"/">"/"&" literais — decodificar reabriria a possibilidade de reintroduzir
        // HTML executável para entradas que contenham entidades (ver docs/seguranca/).
        const string entrada = "Comparação entre 4 < 5 e 10 > 3 usando \"aspas\" & acentuação";

        var resultado = _sut.Sanitizar(entrada);

        Assert.Contains("&lt;", resultado);
        Assert.Contains("&gt;", resultado);
        Assert.Contains("&amp;", resultado);
        Assert.DoesNotContain(" < 5", resultado);
        Assert.DoesNotContain(" > 3", resultado);
    }

    [Fact]
    public void Sanitizar_ComTagCodificadaComoEntidade_NaoReintroduzTagViva()
    {
        // Regressão do achado de segurança: um payload que chega já HTML-encoded
        // (ex.: "&lt;script&gt;") não deve virar uma tag executável na saída.
        var resultado = _sut.Sanitizar("&lt;script&gt;alert(1)&lt;/script&gt;");

        Assert.NotNull(resultado);
        Assert.DoesNotContain("<script", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", resultado, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitizar_ComJavascriptScheme_NaoDeixaHtmlExecutavel()
    {
        var resultado = _sut.Sanitizar("<a href=\"javascript:alert(1)\">clique</a>");

        Assert.NotNull(resultado);
        Assert.DoesNotContain("<a", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", resultado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", resultado);
        Assert.DoesNotContain(">", resultado);
    }
}
