using TccManager.Shared.Models;

namespace TccManager.Shared.DTOs;

// Issue #144 (achado A4): substitui a entidade Acompanhamento crua devolvida por
// GetAcompanhentos/GetDetalhesTcc. Distinto de AcompanhamentoDto (OrientadorDtos.cs), que é
// o corpo de escrita (POST/PUT) e não tem Id.
public class AcompanhamentoResumoDto
{
    public int Id { get; set; }
    public DateTime DataReuniao { get; set; }
    public string Ata { get; set; } = string.Empty;

    public static AcompanhamentoResumoDto DeEntidade(Acompanhamento acompanhamento) => new()
    {
        Id = acompanhamento.Id,
        DataReuniao = acompanhamento.DataReuniao,
        Ata = acompanhamento.Ata
    };
}
