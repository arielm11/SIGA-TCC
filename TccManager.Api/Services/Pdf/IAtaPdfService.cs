namespace TccManager.Api.Services.Pdf;

public enum AtaPdfResultadoStatus
{
    Sucesso,
    BancaNaoEncontrada,
    ResultadoNaoRegistrado,

    /// <summary>
    /// Usado apenas pelo fluxo de rascunho (Etapa 2): <c>Banca.NotaFinal</c> já foi
    /// preenchido, então o rascunho não é mais servido (RNF-03) — mapeado para 410 Gone.
    /// </summary>
    ResultadoJaRegistrado,

    /// <summary>
    /// Issue #72: a banca foi encontrada, mas algum dado relacionado obrigatório (Tcc, Aluno,
    /// ou o Professor/MembroExterno de um avaliador) não carregou apesar do FK indicar que
    /// deveria existir — sinal de inconsistência no banco, não um estado de negócio esperado.
    /// Mapeado para 500 pelos controllers, sem detalhe da inconsistência no corpo da resposta
    /// (o motivo específico fica só no log do servidor).
    /// </summary>
    DadosInconsistentes
}

/// <summary>
/// Resultado da geração do PDF (final ou rascunho) da ata. O <c>Status</c> permite ao
/// controller diferenciar 404 (banca inexistente) de 409/410 (resultado ainda não
/// registrado / já registrado) sem precisar consultar o banco diretamente.
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

    /// <summary>
    /// Gera o PDF rascunho pré-defesa (RF-01/Etapa 2): mesma composição, sem nota final,
    /// sem motivo de reprovação e sem seção de assinaturas, disponível apenas enquanto
    /// <c>Banca.NotaFinal == null</c>. Depois que o resultado é registrado, retorna
    /// <see cref="AtaPdfResultadoStatus.ResultadoJaRegistrado"/> (410 Gone) mesmo que o
    /// chamador ainda tenha um token/sessão tecnicamente válido (RNF-03).
    /// </summary>
    Task<AtaPdfResultado> GerarAtaRascunhoAsync(int idBanca);
}
