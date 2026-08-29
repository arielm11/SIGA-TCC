using System.Globalization;

namespace TccManager.Shared.Formatting;

/// <summary>
/// Formata nota (0-100, uma casa decimal) para exibição em pt-BR ("87,5"), no PDF da ata,
/// nas notificações por e-mail e nas telas do Client — usado tanto pela API quanto pelo
/// Client (Blazor WASM), por isso vive em TccManager.Shared.
///
/// Não usa <see cref="CultureInfo.GetCultureInfo(string)"/>: em modo de globalização
/// invariante (<c>InvariantGlobalization=true</c>, comum em imagens Docker reduzidas para
/// ASP.NET Core/Blazor WASM), buscar uma cultura por nome como "pt-BR" lança
/// <see cref="CultureNotFoundException"/> — só a cultura invariante é suportada nesse modo.
/// Um <see cref="NumberFormatInfo"/> construído diretamente (sem lookup de cultura por nome)
/// funciona igual nos dois modos, então é isso que este formatador usa.
/// </summary>
public static class NotaFormatter
{
    private static readonly NumberFormatInfo FormatoPtBr = new() { NumberDecimalSeparator = "," };

    public static string Formatar(decimal nota) => nota.ToString("0.0", FormatoPtBr);
}
