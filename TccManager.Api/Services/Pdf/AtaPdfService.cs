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

    public AtaPdfService(AppDbContext context, IOptions<AtaInstitucionalOptions> options)
    {
        _context = context;
        _options = options.Value;
    }

    public async Task<AtaPdfResultado> GerarAtaFinalAsync(int idBanca)
    {
        var banca = await CarregarBancaComposicaoAsync(idBanca);

        if (banca == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.BancaNaoEncontrada };

        if (banca.NotaFinal == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.ResultadoNaoRegistrado };

        // Motivo de reprovação (Tcc.MotivoRejeicao) só aparece quando a banca de fato
        // reprovou o TCC — derivado do Status já persistido (fonte de verdade histórica),
        // e não recomputado a partir da nota (ver docs/dados/2026-07-13-pdf-ata-questpdf.md, seção 5).
        var motivoReprovacao = banca.Tcc!.Status == StatusTcc.Reprovado
            ? banca.Tcc.MotivoRejeicao
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

        var model = MontarModel(banca, notaFinal: null, motivoReprovacao: null, rascunho: true);

        var documento = new AtaPdfDocument(model);
        var pdfBytes = documento.GeneratePdf();

        return new AtaPdfResultado { Status = AtaPdfResultadoStatus.Sucesso, PdfBytes = pdfBytes };
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
        var avaliadores = banca.Avaliadores
            .Select(a => a.ProfessorId != null
                ? new AtaMembroBancaModel(a.Professor!.Nome, null)
                : new AtaMembroBancaModel(a.MembroExterno!.Nome, a.MembroExterno!.Instituicao))
            .ToList();

        return new AtaPdfModel(
            Instituicao: _options.Instituicao,
            Curso: _options.Curso,
            TccTitulo: banca.Tcc!.Titulo,
            NomeAluno: banca.Tcc.Aluno!.Nome,
            NomeOrientador: banca.Tcc.Orientador?.Nome ?? "-",
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
