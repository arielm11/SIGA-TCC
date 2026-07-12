using Microsoft.AspNetCore.Hosting;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Variante da <see cref="TccApiFactory"/> que redireciona o WebRootPath do host de teste
/// para um diretório temporário isolado por execução. Os controllers que gravam arquivo
/// (entregas via <c>TccController.EnviarEntrega</c>, atas via
/// <c>CoordenadorController.RegistrarResultadoBanca</c>) resolvem o caminho base através de
/// <c>IStorageService</c>/<c>LocalStorageService</c>, que por sua vez usa
/// <c>IWebHostEnvironment.WebRootPath</c>. Sem esse redirecionamento os arquivos gravados
/// durante os testes iriam para o wwwroot real do projeto TccManager.Api.
///
/// O diretório temporário é criado no construtor e removido no Dispose (mesmo em caso de
/// falha do teste), para não poluir o repositório nem acumular resíduo entre execuções.
/// </summary>
public class WebRootIsolatedApiFactory : TccApiFactory
{
    public readonly string WebRootTemp;

    public WebRootIsolatedApiFactory()
    {
        WebRootTemp = Path.Combine(
            Path.GetTempPath(),
            "siga-tcc-tests",
            "webroot",
            Guid.NewGuid().ToString());
        Directory.CreateDirectory(WebRootTemp);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseWebRoot(WebRootTemp);
    }

    public string PastaUploads(string categoria) => Path.Combine(WebRootTemp, "uploads", categoria);

    public string PastaEntregas => PastaUploads("entregas");

    public string PastaAtas => PastaUploads("atas");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(WebRootTemp))
                    Directory.Delete(WebRootTemp, recursive: true);
            }
            catch
            {
                // Limpeza best-effort: um arquivo eventualmente travado no SO não deve
                // derrubar o teste nem mascarar o resultado real da asserção.
            }
        }
    }
}
