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

        var redigido = path.StartsWith(RascunhoAtaPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"{RascunhoAtaPathPrefix}[REDACTED]"
            : path;

        // Achado CodeQL (CWE-117, "Log entries created from user input", 2026-10-11):
        // Request.Path é controlado pelo cliente — sem isso, um path com \r/\n decodificado
        // forjaria linhas falsas no log (log forging). Remove qualquer caractere de controle
        // antes de qualquer uso em log, nos 4 pontos que passam por este método.
        return redigido.Replace("\r", "").Replace("\n", "");
    }
}
