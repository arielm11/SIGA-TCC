namespace TccManager.Api.Services.Storage;

public interface IStorageService
{
    // Grava o conteúdo e devolve o caminho relativo já no formato persistido/servido hoje:
    //   "/uploads/entregas/{guid}_{nomeSaneado}"  ou  "/uploads/atas/{guid}_{nomeSaneado}"
    Task<string> UploadAsync(
        Stream conteudo,
        string nomeArquivoOriginal,
        CategoriaArquivo categoria,
        CancellationToken cancellationToken = default);

    // Traduz o caminho relativo persistido em uma URL de download.
    Task<string> GetUrlAsync(string caminhoRelativo);

    // Remove o arquivo físico correspondente ao caminho relativo persistido.
    Task DeleteAsync(string caminhoRelativo, CancellationToken cancellationToken = default);
}
