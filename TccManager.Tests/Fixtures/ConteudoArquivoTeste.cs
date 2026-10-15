using System.Text;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #83 (seção 12.1 da arquitetura): fonte única dos "conteúdos de arquivo" usados pelos
/// testes de upload.
///
/// Antes desta issue, <c>CoordenadorController.RegistrarResultadoBanca</c> não validava nada
/// além do tamanho, e vários testes montavam um "PDF falso" de 4 bytes
/// (<c>{ 0x25, 0x50, 0x44, 0x46 }</c> = "%PDF", sem o hífen). Com a validação de magic bytes
/// (D5/D6), a assinatura exigida para <c>.pdf</c> tem 5 bytes ("%PDF-") e aquele literal passou
/// a receber 400 — quebrando ~15 testes que só queriam "um arquivo qualquer" para passar da
/// obrigatoriedade do campo.
///
/// A duplicação do literal em cada arquivo de teste era exatamente o mesmo problema que a
/// extração de <c>AssinaturaArquivoValidator</c> resolveu do lado de produção; centralizar aqui
/// evita que a próxima mudança de assinatura precise de outra varredura pela suíte.
/// </summary>
internal static class ConteudoArquivoTeste
{
    /// <summary>
    /// Assinatura binária mínima e VÁLIDA de um PDF ("%PDF-", 5 bytes). É o menor conteúdo
    /// aceito por <c>AssinaturaArquivoValidator.ValidarAsync(stream, ".pdf")</c>.
    /// </summary>
    public static byte[] AssinaturaPdf => new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

    /// <summary>
    /// PDF (do ponto de vista da validação de cabeçalho) com corpo além da assinatura — útil
    /// quando o teste precisa comparar os bytes gravados/servidos e não apenas o status HTTP.
    /// </summary>
    public static byte[] PdfComCorpo(string corpo) =>
        Encoding.ASCII.GetBytes($"%PDF-1.7\n{corpo}\n%%EOF");

    /// <summary>
    /// "%PDF" sem o hífen: o literal de 4 bytes que a suíte usava antes da issue #83. Mantido
    /// nomeado para os testes que precisam justamente provar que ele é rejeitado.
    /// </summary>
    public static byte[] AssinaturaPdfTruncada => new byte[] { 0x25, 0x50, 0x44, 0x46 };
}
