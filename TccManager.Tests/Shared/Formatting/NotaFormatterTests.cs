using System.Globalization;
using TccManager.Shared.Formatting;
using Xunit;

namespace TccManager.Tests.Shared.Formatting;

/// <summary>
/// Regressão do achado de CI da issue #75: <c>AtaPdfDocument</c>/<c>TccNotificationService</c>/
/// <c>CoordenadorController</c>/<c>BancasConcluidas.razor</c> formatavam a nota com
/// <c>.ToString("0.0")</c> sem cultura explícita — usava
/// <see cref="CultureInfo.CurrentCulture"/> implicitamente, que é "pt-BR" numa máquina Windows
/// configurada em português (dev) mas "en-US"/invariante no runner Linux do GitHub Actions,
/// produzindo "87.5" em vez de "87,5" e derrubando <c>AtaPdfDocumentContentTests</c> só em CI.
/// <see cref="NotaFormatter"/> corrige isso fixando o separador decimal, independente da
/// <see cref="CultureInfo.CurrentCulture"/> vigente no processo.
/// </summary>
public class NotaFormatterTests
{
    [Theory]
    [InlineData(87.5, "87,5")]
    [InlineData(60.0, "60,0")]
    [InlineData(100.0, "100,0")]
    [InlineData(0.0, "0,0")]
    [InlineData(9.95, "10,0")] // arredondamento de "0.0" (MidpointRounding padrão)
    public void Formatar_UsaVirgulaComoSeparadorDecimal(decimal nota, string esperado)
    {
        Assert.Equal(esperado, NotaFormatter.Formatar(nota));
    }

    [Fact]
    public void Formatar_NaoDependeDaCulturaCorrenteDoProcesso()
    {
        // Núcleo da regressão: alterna CurrentCulture entre pt-BR e en-US (a diferença real
        // entre a máquina de desenvolvimento e o runner de CI) e confirma que o resultado não
        // muda — antes da correção, esse teste falharia sob en-US ("87.5" em vez de "87,5").
        var culturaOriginal = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            Assert.Equal("87,5", NotaFormatter.Formatar(87.5m));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("87,5", NotaFormatter.Formatar(87.5m));

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("87,5", NotaFormatter.Formatar(87.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaOriginal;
        }
    }
}
