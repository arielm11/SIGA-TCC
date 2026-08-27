namespace TccManager.Api.Logging;

/// <summary>
/// Redige o path de requisições que carregam uma credencial de portador no próprio path
/// (hoje só /api/rascunho-ata/{token}) antes de qualquer log — mesma regra usada em 3 lugares
/// (UseSerilogRequestLogging em Program.cs, RateLimitingSetup.OnRejected, e o middleware de
/// exceção global), centralizada aqui para não divergir entre eles.
/// </summary>
public static class RequestPathRedactor
{
    private const string RascunhoAtaPathPrefix = "/api/rascunho-ata/";

    public static string Redigir(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "unknown";

        return path.StartsWith(RascunhoAtaPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"{RascunhoAtaPathPrefix}[REDACTED]"
            : path;
    }
}
