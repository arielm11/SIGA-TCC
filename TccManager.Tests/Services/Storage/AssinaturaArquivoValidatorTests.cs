using System.Text;
using TccManager.Api.Services.Storage;
using Xunit;

namespace TccManager.Tests.Services.Storage;

/// <summary>
/// Issue #83 (D5) — testes unitários diretos de <see cref="AssinaturaArquivoValidator"/>.
///
/// A lógica era um par de métodos PRIVADOS de <c>TccController</c>
/// (<c>ValidarAssinaturaArquivoAsync</c>/<c>ObterAssinaturasEsperadas</c>), coberta só
/// indiretamente por HTTP em <see cref="TccManager.Tests.Controllers.TccController_EnviarEntrega_MagicBytes_Tests"/>.
/// A extração para uma classe estática pública permite testar a unidade sem subir o
/// WebApplicationFactory — e, principalmente, provar que a extração NÃO regrediu nada: cada
/// caso aqui corresponde a um caso já coberto via HTTP naquele arquivo (mesmas extensões,
/// mesmas assinaturas, mesmas rejeições).
///
/// O outro objetivo é blindar o contrato do stream (documentado no XML doc do validador):
/// quando o stream de entrada NÃO é seekable, o método devolve um stream diferente, que o
/// chamador precisa descartar — footgun herdado do código original e replicado literalmente
/// nos dois controllers.
/// </summary>
public class AssinaturaArquivoValidatorTests
{
    private static readonly byte[] AssinaturaPdf = { 0x25, 0x50, 0x44, 0x46, 0x2D };
    private static readonly byte[] AssinaturaZip = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] AssinaturaOle = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] ConteudoExecutavel =
        { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };

    /// <summary>
    /// Stream forward-only: reproduz o caso (não esperado em produção, mas tratado pelo
    /// fallback defensivo do validador) em que <c>CanSeek</c> é false.
    /// </summary>
    private sealed class StreamNaoSeekavel : Stream
    {
        private readonly MemoryStream _interno;
        public StreamNaoSeekavel(byte[] conteudo) => _interno = new MemoryStream(conteudo);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _interno.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(bool Valida, Stream Stream)> ValidarAsync(byte[] conteudo, string extensao)
    {
        var origem = new MemoryStream(conteudo);
        return await AssinaturaArquivoValidator.ValidarAsync(origem, extensao);
    }

    // ───────────────────── Aceita a assinatura correta de cada extensão ─────────────────────

    [Fact]
    public async Task Pdf_ComAssinaturaExata_EhValido()
    {
        var (valida, _) = await ValidarAsync(AssinaturaPdf, ".pdf");
        Assert.True(valida);
    }

    [Fact]
    public async Task Pdf_ComAssinaturaMaisCorpo_EhValido()
    {
        var conteudo = Encoding.ASCII.GetBytes("%PDF-1.7\ncorpo do documento\n%%EOF");
        var (valida, _) = await ValidarAsync(conteudo, ".pdf");
        Assert.True(valida);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".zip")]
    public async Task ContainerZip_ComAssinaturaPk_EhValido(string extensao)
    {
        var (valida, _) = await ValidarAsync(AssinaturaZip, extensao);
        Assert.True(valida);
    }

    [Fact]
    public async Task Doc_ComAssinaturaOle_EhValido()
    {
        var conteudo = AssinaturaOle.Concat(Encoding.ASCII.GetBytes("corpo")).ToArray();
        var (valida, _) = await ValidarAsync(conteudo, ".doc");
        Assert.True(valida);
    }

    // ───────────────────── Rejeita conteúdo incoerente com a extensão ─────────────────────

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".zip")]
    [InlineData(".doc")]
    public async Task ExecutavelRenomeado_EhInvalidoParaTodasAsExtensoes(string extensao)
    {
        var (valida, _) = await ValidarAsync(ConteudoExecutavel, extensao);
        Assert.False(valida);
    }

    [Fact]
    public async Task Pdf_ComCabecalhoTruncadoSemHifen_EhInvalido()
    {
        // "%PDF" (4 bytes) não basta: a assinatura de PDF exigida tem 5 ("%PDF-"). É a regra
        // que quebrou os testes existentes de RegistrarResultadoBanca na issue #83.
        var (valida, _) = await ValidarAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 }, ".pdf");
        Assert.False(valida);
    }

    [Fact]
    public async Task Pdf_ComAssinaturaDeZip_EhInvalido()
    {
        var (valida, _) = await ValidarAsync(AssinaturaZip, ".pdf");
        Assert.False(valida);
    }

    [Fact]
    public async Task Docx_ComAssinaturaDePdf_EhInvalido()
    {
        var (valida, _) = await ValidarAsync(AssinaturaPdf, ".docx");
        Assert.False(valida);
    }

    [Fact]
    public async Task Doc_ComAssinaturaOleTruncada_EhInvalido()
    {
        // A assinatura OLE tem 8 bytes; 4 não bastam.
        var (valida, _) = await ValidarAsync(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, ".doc");
        Assert.False(valida);
    }

    [Fact]
    public async Task Doc_ComAssinaturaDeZip_EhInvalido()
    {
        // .doc (OLE) e .docx (OOXML/ZIP) são formatos distintos.
        var (valida, _) = await ValidarAsync(AssinaturaZip, ".doc");
        Assert.False(valida);
    }

    [Fact]
    public async Task StreamVazio_EhInvalido()
    {
        var (valida, _) = await ValidarAsync(Array.Empty<byte>(), ".pdf");
        Assert.False(valida);
    }

    // ───────────────────── Extensão desconhecida ─────────────────────

    [Theory]
    [InlineData(".exe")]
    [InlineData(".txt")]
    [InlineData("")]
    public async Task ExtensaoSemAssinaturaConhecida_EhSempreInvalida(string extensao)
    {
        // ObterAssinaturasEsperadas devolve array vazio -> Any(...) é false. Documenta o
        // comportamento de "fail closed": o validador NUNCA é o que autoriza uma extensão
        // desconhecida; quem filtra extensão é a whitelist do controller, antes desta chamada.
        var (valida, _) = await ValidarAsync(AssinaturaPdf, extensao);
        Assert.False(valida);
    }

    [Fact]
    public async Task ExtensaoEmMaiusculas_NaoEhReconhecida()
    {
        // O switch compara com literais minúsculos: quem chama precisa normalizar com
        // ToLowerInvariant() ANTES (é o que os dois controllers fazem). Este teste fixa a
        // premissa — se alguém remover o ToLowerInvariant() do chamador, um ".PDF" legítimo
        // passaria a ser rejeitado, e é aqui que a causa fica documentada.
        var (valida, _) = await ValidarAsync(AssinaturaPdf, ".PDF");
        Assert.False(valida);
    }

    // ───────────────────── Contrato do stream devolvido ─────────────────────

    [Fact]
    public async Task StreamSeekavel_DevolveOMesmoObjetoEReposicionadoNoInicio()
    {
        // O chamador reusa o stream devolvido para o UploadAsync: ele precisa vir na posição
        // 0, senão o arquivo gravado perderia os primeiros bytes lidos pela validação.
        var conteudo = Encoding.ASCII.GetBytes("%PDF-1.7\nconteudo integral\n%%EOF");
        var origem = new MemoryStream(conteudo);

        var (valida, devolvido) = await AssinaturaArquivoValidator.ValidarAsync(origem, ".pdf");

        Assert.True(valida);
        Assert.Same(origem, devolvido);
        Assert.Equal(0L, devolvido.Position);

        using var copia = new MemoryStream();
        await devolvido.CopyToAsync(copia);
        Assert.Equal(conteudo, copia.ToArray());
    }

    [Fact]
    public async Task StreamNaoSeekavel_DevolveOutroStreamComOConteudoIntegro()
    {
        // Contrato documentado no XML doc: para stream forward-only o método devolve uma cópia
        // em memória — e é responsabilidade do chamador descartá-la (o try/finally com
        // ReferenceEquals que existe em EnviarEntrega e em RegistrarResultadoBanca).
        var conteudo = Encoding.ASCII.GetBytes("%PDF-1.7\nstream forward-only\n%%EOF");
        await using var origem = new StreamNaoSeekavel(conteudo);

        var (valida, devolvido) = await AssinaturaArquivoValidator.ValidarAsync(origem, ".pdf");

        Assert.True(valida);
        Assert.NotSame(origem, devolvido);
        Assert.True(devolvido.CanSeek);
        Assert.Equal(0L, devolvido.Position);

        using var copia = new MemoryStream();
        await devolvido.CopyToAsync(copia);
        Assert.Equal(conteudo, copia.ToArray());

        await devolvido.DisposeAsync();
    }

    [Fact]
    public async Task StreamNaoSeekavel_ComConteudoInvalido_TambemDevolveStreamSubstituto()
    {
        // Mesmo no caminho de rejeição o chamador recebe um stream diferente e precisa
        // descartá-lo — se o finally do controller ficasse dentro do "if (valida)", o
        // MemoryStream vazaria a cada upload rejeitado.
        await using var origem = new StreamNaoSeekavel(ConteudoExecutavel);

        var (valida, devolvido) = await AssinaturaArquivoValidator.ValidarAsync(origem, ".pdf");

        Assert.False(valida);
        Assert.NotSame(origem, devolvido);

        await devolvido.DisposeAsync();
    }

    [Fact]
    public async Task CancellationTokenCancelado_PropagaOperationCanceled()
    {
        // O token é repassado para o CopyToAsync/ReadAsync — a requisição abortada pelo
        // cliente não fica lendo o corpo à toa.
        var origem = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\nconteudo"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AssinaturaArquivoValidator.ValidarAsync(origem, ".pdf", cts.Token));
    }
}
