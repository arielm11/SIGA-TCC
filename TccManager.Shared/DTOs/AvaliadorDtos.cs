namespace TccManager.Shared.DTOs;

public class ConviteBancaDto
{
    public int BancaId { get; set; }
    public string TccTitulo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;
    public string NomeOrientador { get; set; } = string.Empty;
    public DateTime DataHora { get; set; }
    public string Local { get; set; } = string.Empty;

    public string ArquivoFinalCaminho { get; set; } = string.Empty;
}