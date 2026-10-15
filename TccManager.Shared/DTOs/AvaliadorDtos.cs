namespace TccManager.Shared.DTOs;

public class ConviteBancaDto
{
    public int BancaId { get; set; }
    public string TccTitulo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;
    public string NomeOrientador { get; set; } = string.Empty;
    public DateTime DataHora { get; set; }
    public string Local { get; set; } = string.Empty;

    // Id da Entrega Final, usado para baixar o arquivo via GET /api/tcc/entregas/{id}/download
    // (endpoint autenticado — não expõe mais o caminho físico/estático do arquivo).
    public int? ArquivoFinalEntregaId { get; set; }

    // Extensão do arquivo (".pdf", ".doc", ".docx" ou ".zip"), só para nomear o download no
    // Client — a entrega Final não é necessariamente PDF. Nunca o caminho/nome original.
    public string? ArquivoFinalExtensao { get; set; }
}