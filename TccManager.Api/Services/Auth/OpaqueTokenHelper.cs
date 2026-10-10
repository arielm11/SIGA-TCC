using System.Security.Cryptography;
using System.Text;

namespace TccManager.Api.Services.Auth;

/// <summary>
/// Issue #155 (achado B1): geração de token opaco + hash SHA-256 estavam duplicados
/// byte-a-byte entre <c>AuthTokenService</c> e <c>RascunhoAtaTokenService</c> — risco real de
/// divergência se a política de hash mudasse em só um dos dois lugares. Extraído aqui como
/// única implementação compartilhada.
/// </summary>
public static class OpaqueTokenHelper
{
    /// <summary>
    /// CSPRNG (não Guid.NewGuid): o token é uma credencial de portador, precisa de garantia
    /// de imprevisibilidade criptográfica, não apenas unicidade.
    /// </summary>
    public static string GerarTokenBruto() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// SHA-256 em hex, sempre minúsculo — garante comparação confiável de igualdade
    /// independentemente da collation do banco (ver docs/dados).
    /// </summary>
    public static string CalcularHash(string valor)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(valor));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
