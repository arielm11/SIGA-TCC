namespace TccManager.Api.Services.Storage;

/// <summary>
/// RF-05/hardening: valida a assinatura binária (magic bytes) do arquivo enviado contra
/// a extensão declarada, para não confiar apenas no nome do arquivo. PDF exige o prefixo
/// "%PDF-"; DOC (legado) exige a assinatura OLE Compound File; DOCX e ZIP compartilham a
/// mesma assinatura de contêiner ZIP (DOCX é um pacote OOXML) — não é objetivo desta
/// validação diferenciar DOCX de ZIP genérico, apenas rejeitar arquivos que não são
/// nenhum dos tipos esperados.
///
/// Issue #83 (D5): extraído de TccController (era privado, exclusivo de EnviarEntrega)
/// para ser reusado também por CoordenadorController.RegistrarResultadoBanca. Estático,
/// sem DI — mesmo precedente de BrasiliaTimeZoneService: função pura, sem estado, sem
/// dependência.
/// </summary>
public static class AssinaturaArquivoValidator
{
    /// <summary>
    /// Valida a assinatura binária do stream contra a extensão informada.
    ///
    /// Contrato importante para o chamador (footgun real, herdado do código original):
    /// quando <paramref name="streamOriginal"/> não é seekable, este método devolve um
    /// stream DIFERENTE (uma cópia em memória) em <c>StreamParaUpload</c>. O chamador é
    /// responsável por descartar esse stream substituto quando ele não for o mesmo
    /// objeto do stream original, tipicamente com:
    /// <code>
    /// finally
    /// {
    ///     if (!ReferenceEquals(streamParaUpload, streamOriginal))
    ///         await streamParaUpload.DisposeAsync();
    /// }
    /// </code>
    /// </summary>
    public static async Task<(bool AssinaturaValida, Stream StreamParaUpload)> ValidarAsync(
        Stream streamOriginal, string extensao, CancellationToken cancellationToken = default)
    {
        var streamSeekavel = streamOriginal;

        // arquivo.OpenReadStream() de um IFormFile é seekable no caso normal (buffer em
        // memória/disco pelo model binding); fallback defensivo para o caso (não esperado
        // em produção) de um stream forward-only.
        if (!streamOriginal.CanSeek)
        {
            var buffer = new MemoryStream();
            await streamOriginal.CopyToAsync(buffer, cancellationToken);
            buffer.Seek(0, SeekOrigin.Begin);
            streamSeekavel = buffer;
        }

        var assinaturasEsperadas = ObterAssinaturasEsperadas(extensao);
        var tamanhoCabecalho = assinaturasEsperadas.Length == 0 ? 0 : assinaturasEsperadas.Max(a => a.Length);
        var cabecalho = new byte[tamanhoCabecalho];
        var totalLido = tamanhoCabecalho == 0
            ? 0
            : await streamSeekavel.ReadAsync(cabecalho.AsMemory(0, tamanhoCabecalho), cancellationToken);

        streamSeekavel.Seek(0, SeekOrigin.Begin);

        var assinaturaValida = assinaturasEsperadas.Any(assinatura =>
            totalLido >= assinatura.Length && cabecalho.AsSpan(0, assinatura.Length).SequenceEqual(assinatura));

        return (assinaturaValida, streamSeekavel);
    }

    private static byte[][] ObterAssinaturasEsperadas(string extensao) => extensao switch
    {
        ".pdf" => new[] { new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D } }, // "%PDF-"
        ".docx" => new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } }, // ZIP/OOXML
        ".zip" => new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } }, // ZIP
        ".doc" => new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } }, // OLE Compound File
        _ => Array.Empty<byte[]>()
    };
}
