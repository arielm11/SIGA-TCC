namespace TccManager.Api.Services.Storage;

public class LocalStorageService : IStorageService
{
    private readonly IWebHostEnvironment _env;

    public LocalStorageService(IWebHostEnvironment env)
    {
        _env = env;
    }

    public async Task<string> UploadAsync(
        Stream conteudo,
        string nomeArquivoOriginal,
        CategoriaArquivo categoria,
        CancellationToken cancellationToken = default)
    {
        var pastaCategoria = ObterNomePasta(categoria);
        var uploadsFolder = Path.Combine(ObterWebRootBase(), "uploads", pastaCategoria);

        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        var nomeSaneado = Path.GetFileName(nomeArquivoOriginal);
        var fileName = $"{Guid.NewGuid()}_{nomeSaneado}";
        var filePath = Path.Combine(uploadsFolder, fileName);

        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await conteudo.CopyToAsync(stream, cancellationToken);
        }

        return $"/uploads/{pastaCategoria}/{fileName}";
    }

    public Task<string> GetUrlAsync(string caminhoRelativo)
    {
        return Task.FromResult(caminhoRelativo);
    }

    public Task DeleteAsync(string caminhoRelativo, CancellationToken cancellationToken = default)
    {
        var webRootBase = Path.GetFullPath(ObterWebRootBase());
        var uploadsDir = Path.Combine(webRootBase, "uploads");
        var caminhoRelativoNormalizado = caminhoRelativo.TrimStart('/', '\\');
        var caminhoCompleto = Path.GetFullPath(Path.Combine(webRootBase, caminhoRelativoNormalizado));

        // Confina a exclusão à pasta de uploads: rejeita qualquer ".." que escape
        // dela, mesmo que caminhoRelativo venha de uma fonte não confiável no futuro.
        if (!caminhoCompleto.StartsWith(uploadsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Caminho fora do diretório de uploads.");

        if (File.Exists(caminhoCompleto))
            File.Delete(caminhoCompleto);

        return Task.CompletedTask;
    }

    private string ObterWebRootBase()
    {
        return _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    }

    private static string ObterNomePasta(CategoriaArquivo categoria) => categoria switch
    {
        CategoriaArquivo.Entregas => "entregas",
        CategoriaArquivo.Atas => "atas",
        _ => throw new ArgumentOutOfRangeException(nameof(categoria), categoria, "Categoria de arquivo desconhecida.")
    };
}
