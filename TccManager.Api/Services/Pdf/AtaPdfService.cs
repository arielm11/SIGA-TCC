using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using TccManager.Api.Data;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Orquestra a geração do PDF (final e rascunho) da ata: carrega os dados via EF Core
/// (única query, AsNoTracking — leitura pontual, ver docs/dados/2026-07-13-pdf-ata-questpdf.md
/// e docs/dados/2026-07-13-pdf-ata-rascunho-etapa2.md), monta o <see cref="AtaPdfModel"/>
/// resolvendo o polimorfismo de <c>BancaAvaliador</c> e a conversão de fuso, e delega o
/// layout ao <see cref="AtaPdfDocument"/> (QuestPDF). Não conhece a fluent API do QuestPDF
/// diretamente — apenas invoca <c>GeneratePdf()</c> sobre o documento.
/// </summary>
public class AtaPdfService : IAtaPdfService
{
    private readonly AppDbContext _context;
    private readonly AtaInstitucionalOptions _options;
    private readonly ILogger<AtaPdfService> _logger;

    public AtaPdfService(AppDbContext context, IOptions<AtaInstitucionalOptions> options, ILogger<AtaPdfService> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AtaPdfResultado> GerarAtaFinalAsync(int idBanca)
    {
        var banca = await CarregarBancaComposicaoAsync(idBanca);

        if (banca == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.BancaNaoEncontrada };

        if (banca.NotaFinal == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.ResultadoNaoRegistrado };

        if (!TentarValidarConsistencia(banca, idBanca, out var motivoInconsistencia))
        {
            _logger.LogError(
                "Dados inconsistentes ao gerar a ata final da banca {BancaId}: {MotivoInconsistencia}",
                idBanca, motivoInconsistencia);
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.DadosInconsistentes };
        }

        // Motivo de reprovação (Tcc.MotivoRejeicao) só aparece quando a banca de fato
        // reprovou o TCC — derivado do Status já persistido (fonte de verdade histórica),
        // e não recomputado a partir da nota (ver docs/dados/2026-07-13-pdf-ata-questpdf.md, seção 5).
        // TentarValidarConsistencia acima já garante banca.Tcc não nulo; o "?? throw" é só um
        // assert de invariante (issue #72), nunca deveria disparar de verdade.
        var tcc = banca.Tcc
            ?? throw new InvalidOperationException($"Banca {idBanca}: Tcc nulo apesar da validação de consistência.");
        var motivoReprovacao = tcc.Status == StatusTcc.Reprovado
            ? tcc.MotivoRejeicao
            : null;

        var model = MontarModel(banca, notaFinal: banca.NotaFinal, motivoReprovacao: motivoReprovacao, rascunho: false);

        var documento = new AtaPdfDocument(model);
        var pdfBytes = documento.GeneratePdf();

        return new AtaPdfResultado { Status = AtaPdfResultadoStatus.Sucesso, PdfBytes = pdfBytes };
    }

    public async Task<AtaPdfResultado> GerarAtaRascunhoAsync(int idBanca)
    {
        var banca = await CarregarBancaComposicaoAsync(idBanca);

        if (banca == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.BancaNaoEncontrada };

        // Bloqueio definitivo (RNF-03): uma vez registrado o resultado, o rascunho não é
        // mais servido — mesmo que o chamador ainda tenha um token/sessão válido por data.
        if (banca.NotaFinal != null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.ResultadoJaRegistrado };

        if (!TentarValidarConsistencia(banca, idBanca, out var motivoInconsistencia))
        {
            _logger.LogError(
                "Dados inconsistentes ao gerar a ata rascunho da banca {BancaId}: {MotivoInconsistencia}",
                idBanca, motivoInconsistencia);
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.DadosInconsistentes };
        }

        var model = MontarModel(banca, notaFinal: null, motivoReprovacao: null, rascunho: true);

        var documento = new AtaPdfDocument(model);
        var pdfBytes = documento.GeneratePdf();

        return new AtaPdfResultado { Status = AtaPdfResultadoStatus.Sucesso, PdfBytes = pdfBytes };
    }

    /// <summary>
    /// Issue #72: <c>Banca.TccId</c>, <c>Tcc.AlunoId</c> e os FKs de avaliador em
    /// <c>BancaAvaliador</c> são obrigatórios no schema (constraints de FK do banco), então em
    /// operação normal as navegações correspondentes nunca deveriam vir nulas depois do
    /// <see cref="CarregarBancaComposicaoAsync"/>. Mas nada nesta camada garante isso de fato —
    /// dado inconsistente (linha órfã por edição manual do banco, migração pendente, etc.) antes
    /// virava <see cref="NullReferenceException"/> não tratada em <see cref="MontarModel"/>.
    /// Validar aqui devolve um status específico e diagnosticável (<see
    /// cref="AtaPdfResultadoStatus.DadosInconsistentes"/>, mapeado para 500 pelos controllers) em
    /// vez de deixar a exceção genérica do <c>GlobalExceptionHandler</c> (issue #71) ser a única
    /// rede de segurança, sem indicar qual relação estava inconsistente.
    /// </summary>
    private static bool TentarValidarConsistencia(Banca banca, int idBanca, out string? motivoInconsistencia)
    {
        if (banca.Tcc is null)
        {
            motivoInconsistencia = $"Banca {idBanca}: Tcc (TccId={banca.TccId}) não carregou.";
            return false;
        }

        if (banca.Tcc.Aluno is null)
        {
            motivoInconsistencia = $"Banca {idBanca}: Tcc.Aluno (AlunoId={banca.Tcc.AlunoId}) não carregou.";
            return false;
        }

        // OrientadorId é FK opcional (Tcc sem orientador atribuído é estado de negócio válido,
        // vira "-" na ata) — mas se preenchido e a navegação não carregou, é a mesma classe de
        // inconsistência dos demais campos, só que silenciosa até aqui: MontarModel usa
        // "?.Nome ?? "-"", que mascararia o orientador órfão como "sem orientador".
        if (banca.Tcc.OrientadorId is not null && banca.Tcc.Orientador is null)
        {
            motivoInconsistencia = $"Banca {idBanca}: Tcc.Orientador (OrientadorId={banca.Tcc.OrientadorId}) não carregou.";
            return false;
        }

        // Ata sem nenhum avaliador é um documento oficial estruturalmente inválido (sem
        // membros de banca, sem linhas de assinatura) — o loop abaixo simplesmente não roda
        // nesse caso, então o check precisa ser explícito.
        if (banca.Avaliadores.Count == 0)
        {
            motivoInconsistencia = $"Banca {idBanca}: nenhum BancaAvaliador associado.";
            return false;
        }

        foreach (var avaliador in banca.Avaliadores)
        {
            if (avaliador.ProfessorId is null && avaliador.MembroExternoId is null)
            {
                motivoInconsistencia = $"Banca {idBanca}: BancaAvaliador {avaliador.Id} sem Professor nem MembroExterno.";
                return false;
            }

            // Exatamente um dos dois, não "pelo menos um": com os dois preenchidos,
            // MontarModel escolhe o Professor e descarta o MembroExterno em silêncio — o
            // avaliador externo sumiria da ata sem nenhum log.
            if (avaliador.ProfessorId is not null && avaliador.MembroExternoId is not null)
            {
                motivoInconsistencia = $"Banca {idBanca}: BancaAvaliador {avaliador.Id} tem Professor E MembroExterno preenchidos (deveria ser exatamente um).";
                return false;
            }

            if (avaliador.ProfessorId is not null && avaliador.Professor is null)
            {
                motivoInconsistencia = $"Banca {idBanca}: BancaAvaliador {avaliador.Id}.Professor (ProfessorId={avaliador.ProfessorId}) não carregou.";
                return false;
            }

            if (avaliador.MembroExternoId is not null && avaliador.MembroExterno is null)
            {
                motivoInconsistencia = $"Banca {idBanca}: BancaAvaliador {avaliador.Id}.MembroExterno (MembroExternoId={avaliador.MembroExternoId}) não carregou.";
                return false;
            }
        }

        motivoInconsistencia = null;
        return true;
    }

    private Task<Banca?> CarregarBancaComposicaoAsync(int idBanca) =>
        _context.Banca
            .AsNoTracking()
            .Include(b => b.Tcc).ThenInclude(t => t!.Aluno)
            .Include(b => b.Tcc).ThenInclude(t => t!.Orientador)
            .Include(b => b.Avaliadores).ThenInclude(a => a.Professor)
            .Include(b => b.Avaliadores).ThenInclude(a => a.MembroExterno)
            .FirstOrDefaultAsync(b => b.Id == idBanca);

    private AtaPdfModel MontarModel(Banca banca, decimal? notaFinal, string? motivoReprovacao, bool rascunho)
    {
        // TentarValidarConsistencia já garante Tcc/Aluno/avaliadores não nulos antes de chamar
        // este método; os "?? throw" abaixo são asserts de invariante (issue #72), não o
        // caminho normal de erro — nunca deveriam disparar de verdade.
        var tcc = banca.Tcc
            ?? throw new InvalidOperationException($"Banca {banca.Id}: Tcc nulo apesar da validação de consistência.");
        var aluno = tcc.Aluno
            ?? throw new InvalidOperationException($"Banca {banca.Id}: Tcc.Aluno nulo apesar da validação de consistência.");

        var avaliadores = banca.Avaliadores
            .Select(a =>
            {
                if (a.ProfessorId != null)
                {
                    var professor = a.Professor
                        ?? throw new InvalidOperationException($"BancaAvaliador {a.Id}: Professor nulo apesar da validação de consistência.");
                    return new AtaMembroBancaModel(professor.Nome, null);
                }

                var membroExterno = a.MembroExterno
                    ?? throw new InvalidOperationException($"BancaAvaliador {a.Id}: MembroExterno nulo apesar da validação de consistência.");
                return new AtaMembroBancaModel(membroExterno.Nome, membroExterno.Instituicao);
            })
            .ToList();

        return new AtaPdfModel(
            Instituicao: _options.Instituicao,
            Curso: _options.Curso,
            TccTitulo: tcc.Titulo,
            NomeAluno: aluno.Nome,
            NomeOrientador: tcc.Orientador?.Nome ?? "-",
            Avaliadores: avaliadores,
            DataHoraDefesaBrasilia: BrasiliaTimeZoneService.ConverterDeUtcParaBrasilia(banca.DataHora),
            Local: banca.Local,
            NotaFinal: notaFinal,
            MotivoReprovacao: motivoReprovacao,
            DataGeracaoBrasilia: BrasiliaTimeZoneService.ConverterDeUtcParaBrasilia(DateTime.UtcNow),
            Rascunho: rascunho
        );
    }
}
