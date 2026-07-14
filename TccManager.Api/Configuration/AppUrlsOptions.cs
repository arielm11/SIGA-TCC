namespace TccManager.Api.Configuration;

/// <summary>
/// URLs base usadas para montar links absolutos em e-mails (N2 Etapa 2): o link do
/// rascunho com token para o membro externo (<c>PublicApiBaseUrl</c>) e o atalho de
/// login para os avaliadores internos (<c>ClientBaseUrl</c>). Bindada da seção "App".
/// </summary>
public class AppUrlsOptions
{
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string ClientBaseUrl { get; set; } = string.Empty;
}
