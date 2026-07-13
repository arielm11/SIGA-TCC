namespace TccManager.Api.Services.Pdf;

public enum AtaPdfResultadoStatus
{
    Sucesso,
    BancaNaoEncontrada,
    ResultadoNaoRegistrado
}

/// <summary>
/// Resultado da geração do PDF final da ata. O <c>Status</c> permite ao controller
/// diferenciar 404 (banca inexistente) de 409 (resultado ainda não registrado) sem
/// precisar consultar o banco diretamente (ver RF-03/seção 5 da arquitetura).
/// </summary>
public class AtaPdfResultado
{
    public required AtaPdfResultadoStatus Status { get; init; }
    public byte[]? PdfBytes { get; init; }
}

public interface IAtaPdfService
{
    /// <summary>
    /// Gera o PDF final da ata (pós-resultado) para a banca informada, carregando os
    /// dados já persistidos via EF Core. Não gera PDF parcial: se a banca não existir
    /// ou o resultado ainda não tiver sido registrado (<c>NotaFinal == null</c>), o
    /// <see cref="AtaPdfResultado.Status"/> indica o motivo e <c>PdfBytes</c> vem nulo.
    /// </summary>
    Task<AtaPdfResultado> GerarAtaFinalAsync(int idBanca);
}
