using System.Text;

namespace TccManager.Shared.Validation;

/// <summary>
/// Política mínima de senha (issue #88, RF-04), aplicada apenas à credencial de bootstrap de
/// Admin (<c>AdminBootstrapSetup</c>) e à troca de senha obrigatória
/// (<c>POST /api/auth/trocar-senha-obrigatoria</c>) — deliberadamente NÃO aplicada a
/// <c>POST /api/usuario</c>/<c>PUT /api/usuario/{id}</c> (fora de escopo desta issue, ver
/// P-05 em docs/arquitetura/2026-09-03-bootstrap-primeiro-admin.md).
///
/// Classe estática pura, sem dependências — mesma natureza de
/// <c>Formatting/NotaFormatter</c>/<c>Formatting/StatusTccFormatter</c>, usada tanto pela API
/// quanto pelo Client (validação de servidor é sempre a autoridade; o Client só replica para
/// UX).
/// </summary>
public static class PoliticaSenha
{
    public const int MinimoCaracteres = 12;

    // BCrypt trunca silenciosamente em 72 bytes — sem este teto, uma senha maior seria
    // aceita e só os 72 primeiros bytes valeriam para autenticação (diferença invisível
    // entre o que o operador configurou/digitou e o que de fato autentica).
    public const int MaximoBytesUtf8 = 72;

    /// <summary>
    /// Valida a senha de bootstrap (sem senha atual para comparar).
    /// </summary>
    public static bool Valida(string senha, string email, out string? motivo) =>
        Valida(senha, email, senhaAtual: null, out motivo);

    /// <summary>
    /// Valida a senha, opcionalmente comparando com a senha atual (fluxo de troca forçada,
    /// para impedir trocar a senha temporária por ela mesma).
    /// </summary>
    public static bool Valida(string senha, string email, string? senhaAtual, out string? motivo)
    {
        if (string.IsNullOrWhiteSpace(senha))
        {
            motivo = "A senha não pode ser vazia.";
            return false;
        }

        if (senha.Length < MinimoCaracteres)
        {
            motivo = $"A senha deve ter no mínimo {MinimoCaracteres} caracteres.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(senha) > MaximoBytesUtf8)
        {
            motivo = $"A senha deve ter no máximo {MaximoBytesUtf8} bytes (limite do BCrypt).";
            return false;
        }

        if (SenhaIgualEmail(senha, email))
        {
            motivo = "A senha não pode ser igual ao e-mail.";
            return false;
        }

        if (senhaAtual != null && string.Equals(senha, senhaAtual, StringComparison.Ordinal))
        {
            motivo = "A nova senha não pode ser igual à senha atual.";
            return false;
        }

        motivo = null;
        return true;
    }

    private static bool SenhaIgualEmail(string senha, string email)
    {
        if (string.IsNullOrEmpty(email))
            return false;

        if (string.Equals(senha, email, StringComparison.OrdinalIgnoreCase))
            return true;

        var parteLocal = email.Split('@', 2)[0];
        return string.Equals(senha, parteLocal, StringComparison.OrdinalIgnoreCase);
    }
}
