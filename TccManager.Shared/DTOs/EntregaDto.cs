using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Shared.DTOs;

// Issue #144 (achado B7): substitui a entidade Entrega crua. ExtensaoArquivo (ex.: ".pdf")
// é o suficiente para o Client nomear o arquivo baixado — não expõe o caminho interno de
// armazenamento nem o nome original gravado em disco (ArquivoCaminho).
public class EntregaDto
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public TipoEntrega Tipo { get; set; }
    public StatusEntrega Status { get; set; }
    public DateTime DataEnvio { get; set; }
    public string? Feedback { get; set; }
    public decimal? Nota { get; set; }
    public string? ExtensaoArquivo { get; set; }

    public static EntregaDto DeEntidade(Entrega entrega) => new()
    {
        Id = entrega.Id,
        Titulo = entrega.Titulo,
        Tipo = entrega.Tipo,
        Status = entrega.Status,
        DataEnvio = entrega.DataEnvio,
        Feedback = entrega.Feedback,
        Nota = entrega.Nota,
        ExtensaoArquivo = string.IsNullOrEmpty(entrega.ArquivoCaminho)
            ? null
            : System.IO.Path.GetExtension(entrega.ArquivoCaminho)
    };
}
