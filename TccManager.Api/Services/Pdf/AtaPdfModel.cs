namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Um membro (avaliador) da banca já resolvido a partir do polimorfismo de
/// <see cref="TccManager.Shared.Models.BancaAvaliador"/>: <c>Instituicao</c> é nulo/vazio
/// para avaliadores internos (professores) e preenchido para membros externos.
/// </summary>
public record AtaMembroBancaModel(string Nome, string? Instituicao);

/// <summary>
/// View model interno da API para o template do PDF da ata (RF-02). Desacoplado dos
/// models EF: não conhece <c>AppDbContext</c> nem navegações — toda a resolução do
/// polimorfismo de <c>BancaAvaliador</c> e a conversão de fuso (Brasília) já vêm prontas.
/// </summary>
public record AtaPdfModel(
    string Instituicao,
    string Curso,
    string TccTitulo,
    string NomeAluno,
    string NomeOrientador,
    IReadOnlyList<AtaMembroBancaModel> Avaliadores,
    DateTime DataHoraDefesaBrasilia,
    string Local,
    decimal NotaFinal,
    string? MotivoReprovacao,
    DateTime DataGeracaoBrasilia
);
