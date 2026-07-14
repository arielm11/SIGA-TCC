namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Um membro (avaliador) da banca já resolvido a partir do polimorfismo de
/// <see cref="TccManager.Shared.Models.BancaAvaliador"/>: <c>Instituicao</c> é nulo/vazio
/// para avaliadores internos (professores) e preenchido para membros externos.
/// </summary>
public record AtaMembroBancaModel(string Nome, string? Instituicao);

/// <summary>
/// View model interno da API para o template do PDF da ata (RF-02/Etapa 1, RF-01/Etapa 2).
/// Desacoplado dos models EF: não conhece <c>AppDbContext</c> nem navegações — toda a
/// resolução do polimorfismo de <c>BancaAvaliador</c> e a conversão de fuso (Brasília) já
/// vêm prontas. <c>Rascunho</c> ramifica o layout no <see cref="AtaPdfDocument"/>: quando
/// <c>true</c>, <c>NotaFinal</c> e <c>MotivoReprovacao</c> vêm nulos (o resultado ainda não
/// existe) e a seção de assinaturas é omitida por completo (ver
/// docs/arquitetura/2026-07-13-pdf-ata-rascunho-etapa2.md, seção 4).
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
    decimal? NotaFinal,
    string? MotivoReprovacao,
    DateTime DataGeracaoBrasilia,
    bool Rascunho = false
);
