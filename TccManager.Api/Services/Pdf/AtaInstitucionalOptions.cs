namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Options bindadas da seção "Ata" da configuração. Cabeçalho institucional exibido no
/// PDF da ata (RF-06) — nunca hardcoded no template. Logo/imagem fica fora de escopo
/// nesta etapa (ver docs/arquitetura/2026-07-13-pdf-ata-questpdf.md, seção 6).
/// </summary>
public class AtaInstitucionalOptions
{
    public string Instituicao { get; set; } = string.Empty;
    public string Curso { get; set; } = string.Empty;
}
