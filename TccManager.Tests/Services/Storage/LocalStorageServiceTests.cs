using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using TccManager.Api.Services.Storage;

namespace TccManager.Tests.Services.Storage;

/// <summary>
/// Testes unitários diretos de <see cref="LocalStorageService"/>: instanciam o serviço com um
/// <see cref="IWebHostEnvironment"/> fake apontando para um diretório temporário isolado, sem
/// subir o host via WebApplicationFactory. Cada teste usa sua própria pasta temporária e a
/// remove no <see cref="Dispose"/>, para nunca tocar o wwwroot real do projeto.
/// </summary>
public class LocalStorageServiceTests : IDisposable
{
    private readonly string _webRootTemp;
    private readonly LocalStorageService _sut;

    public LocalStorageServiceTests()
    {
        _webRootTemp = Path.Combine(
            Path.GetTempPath(),
            "siga-tcc-tests",
            "unit-storage",
            Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webRootTemp);

        _sut = new LocalStorageService(new FakeWebHostEnvironment(_webRootTemp));
    }

    private static Stream ConteudoDe(string texto) => new MemoryStream(Encoding.UTF8.GetBytes(texto));

    [Fact]
    public async Task UploadAsync_ComEntrega_GravaArquivoFisicoERetornaCaminhoRelativoEsperado()
    {
        const string conteudo = "conteudo da entrega";
        await using var stream = ConteudoDe(conteudo);

        var caminhoRelativo = await _sut.UploadAsync(stream, "monografia.pdf", CategoriaArquivo.Entregas);

        // Formato do caminho relativo: "/uploads/{categoria}/{guid}_{nomeSaneado}".
        Assert.StartsWith("/uploads/entregas/", caminhoRelativo);
        Assert.EndsWith("_monografia.pdf", caminhoRelativo);

        var nomeArquivo = caminhoRelativo.Substring("/uploads/entregas/".Length);
        var caminhoFisico = Path.Combine(_webRootTemp, "uploads", "entregas", nomeArquivo);
        Assert.True(File.Exists(caminhoFisico), "O arquivo deveria ter sido gravado fisicamente na pasta de entregas.");
        Assert.Equal(conteudo, await File.ReadAllTextAsync(caminhoFisico));

        // O prefixo antes de "_monografia.pdf" deve ser um GUID válido.
        var prefixoGuid = nomeArquivo.Substring(0, nomeArquivo.Length - "_monografia.pdf".Length);
        Assert.True(Guid.TryParse(prefixoGuid, out _), $"Prefixo '{prefixoGuid}' deveria ser um GUID válido.");
    }

    [Fact]
    public async Task UploadAsync_ChamadasSucessivas_GeramNomesUnicos()
    {
        await using var s1 = ConteudoDe("a");
        await using var s2 = ConteudoDe("b");

        var caminho1 = await _sut.UploadAsync(s1, "arquivo.pdf", CategoriaArquivo.Entregas);
        var caminho2 = await _sut.UploadAsync(s2, "arquivo.pdf", CategoriaArquivo.Entregas);

        Assert.NotEqual(caminho1, caminho2);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_webRootTemp, "uploads", "entregas")).Length);
    }

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("../../../wwwroot/Program.cs")]
    public async Task UploadAsync_ComNomeContendoPathTraversal_NeutralizaComPathGetFileName(string nomeMalicioso)
    {
        // Nota de portabilidade: Path.GetFileName só trata "\" como separador no Windows;
        // no Linux (runner do CI), "\" é um caractere literal de nome de arquivo. Por isso o
        // "nome esperado" é calculado com o mesmo Path.GetFileName usado em produção, em vez
        // de um literal hardcoded — a propriedade de segurança verificada não é "o nome vira
        // X", e sim "o arquivo nunca escapa da pasta da categoria", que vale nas duas plataformas.
        var nomeEsperado = Path.GetFileName(nomeMalicioso);

        await using var stream = ConteudoDe("payload");

        var caminhoRelativo = await _sut.UploadAsync(stream, nomeMalicioso, CategoriaArquivo.Entregas);

        // Regressão de segurança: o caminho retornado nunca deve escapar da pasta da categoria.
        Assert.StartsWith("/uploads/entregas/", caminhoRelativo);
        Assert.EndsWith($"_{nomeEsperado}", caminhoRelativo);

        var pastaCategoria = Path.GetFullPath(Path.Combine(_webRootTemp, "uploads", "entregas"));

        // O arquivo físico deve estar estritamente dentro da pasta de entregas.
        var arquivosGravados = Directory.GetFiles(pastaCategoria);
        Assert.Single(arquivosGravados);

        var caminhoFisicoResolvido = Path.GetFullPath(arquivosGravados[0]);
        Assert.StartsWith(pastaCategoria + Path.DirectorySeparatorChar, caminhoFisicoResolvido);
        Assert.EndsWith($"_{nomeEsperado}", Path.GetFileName(caminhoFisicoResolvido));

        // Nada foi criado fora da pasta de uploads (nenhum escape para o webroot ou acima).
        var conteudoWebRoot = Directory.GetFileSystemEntries(_webRootTemp);
        Assert.Single(conteudoWebRoot);
        Assert.Equal(Path.Combine(_webRootTemp, "uploads"), conteudoWebRoot[0]);
    }

    [Fact]
    public async Task UploadAsync_CategoriasDiferentes_GravamEmSubpastasDiferentes()
    {
        await using var streamEntrega = ConteudoDe("entrega");
        await using var streamAta = ConteudoDe("ata");

        var caminhoEntrega = await _sut.UploadAsync(streamEntrega, "entrega.pdf", CategoriaArquivo.Entregas);
        var caminhoAta = await _sut.UploadAsync(streamAta, "ata.pdf", CategoriaArquivo.Atas);

        Assert.StartsWith("/uploads/entregas/", caminhoEntrega);
        Assert.StartsWith("/uploads/atas/", caminhoAta);

        var pastaEntregas = Path.Combine(_webRootTemp, "uploads", "entregas");
        var pastaAtas = Path.Combine(_webRootTemp, "uploads", "atas");

        Assert.Single(Directory.GetFiles(pastaEntregas));
        Assert.Single(Directory.GetFiles(pastaAtas));
    }

    [Fact]
    public async Task DeleteAsync_ComArquivoExistente_RemoveArquivoFisico()
    {
        await using var stream = ConteudoDe("para deletar");
        var caminhoRelativo = await _sut.UploadAsync(stream, "temporario.pdf", CategoriaArquivo.Atas);

        var nomeArquivo = caminhoRelativo.Substring("/uploads/atas/".Length);
        var caminhoFisico = Path.Combine(_webRootTemp, "uploads", "atas", nomeArquivo);
        Assert.True(File.Exists(caminhoFisico));

        await _sut.DeleteAsync(caminhoRelativo);

        Assert.False(File.Exists(caminhoFisico), "O arquivo deveria ter sido removido por DeleteAsync.");
    }

    [Fact]
    public async Task DeleteAsync_ComArquivoInexistente_NaoLancaExcecao()
    {
        var caminhoInexistente = $"/uploads/entregas/{Guid.NewGuid()}_nao-existe.pdf";

        var excecao = await Record.ExceptionAsync(() => _sut.DeleteAsync(caminhoInexistente));

        Assert.Null(excecao);
    }

    [Theory]
    [InlineData("../../fora-do-uploads.txt")]
    [InlineData("/uploads/../../fora-do-uploads.txt")]
    [InlineData("uploads/../../../fora-do-uploads.txt")]
    public async Task DeleteAsync_ComCaminhoQueEscapaDaPastaDeUploads_LancaInvalidOperationException(
        string caminhoMalicioso)
    {
        // Regressão de segurança: um arquivo fora de wwwroot/uploads criado propositalmente
        // para este teste NUNCA deve ser removido por um caminho que tente escapar via "..".
        var caminhoForaDoUploads = Path.Combine(_webRootTemp, "fora-do-uploads.txt");
        await File.WriteAllTextAsync(caminhoForaDoUploads, "não deveria ser apagado");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.DeleteAsync(caminhoMalicioso));

        Assert.True(File.Exists(caminhoForaDoUploads), "DeleteAsync não deveria ter alcançado um arquivo fora de wwwroot/uploads.");
    }

    [Fact]
    public async Task GetUrlAsync_RetornaOMesmoCaminhoRelativo_Identidade()
    {
        const string caminho = "/uploads/atas/abc_ata.pdf";

        var url = await _sut.GetUrlAsync(caminho);

        Assert.Equal(caminho, url);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_webRootTemp))
                Directory.Delete(_webRootTemp, recursive: true);
        }
        catch
        {
            // Limpeza best-effort: um arquivo eventualmente travado no SO não deve
            // derrubar o teste nem mascarar o resultado real da asserção.
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string webRootPath)
        {
            WebRootPath = webRootPath;
            ContentRootPath = webRootPath;
            WebRootFileProvider = new PhysicalFileProvider(webRootPath);
            ContentRootFileProvider = new PhysicalFileProvider(webRootPath);
        }

        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string ApplicationName { get; set; } = "TccManager.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }
}
