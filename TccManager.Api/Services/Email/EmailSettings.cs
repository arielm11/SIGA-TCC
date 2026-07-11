namespace TccManager.Api.Services.Email;

/// <summary>
/// Options bindadas da seção "Email" da configuração. Em desenvolvimento, os valores
/// reais (host, porta, usuário, senha, remetente) vêm de User Secrets — o esqueleto
/// versionado em appsettings.json fica vazio, mesmo padrão já usado para Jwt:Key.
/// </summary>
public class EmailSettings
{
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>
    /// Remetente completo (nome + endereço), ex.: "SIGA-TCC &lt;noreply@siga-tcc.local&gt;".
    /// </summary>
    public string From { get; set; } = string.Empty;
}

public class SmtpSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    public string? User { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; }
}
