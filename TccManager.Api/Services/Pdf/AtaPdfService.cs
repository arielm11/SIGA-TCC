using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using TccManager.Api.Data;
using TccManager.Shared.Enums;

namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Orquestra a geração do PDF final da ata: carrega os dados via EF Core (única query,
/// AsNoTracking — leitura pontual, ver docs/dados/2026-07-13-pdf-ata-questpdf.md), monta o
/// <see cref="AtaPdfModel"/> resolvendo o polimorfismo de <c>BancaAvaliador</c> e a conversão
/// de fuso, e delega o layout ao <see cref="AtaPdfDocument"/> (QuestPDF). Não conhece a
/// fluent API do QuestPDF diretamente — apenas invoca <c>GeneratePdf()</c> sobre o documento.
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
        var banca = await _context.Banca
            .AsNoTracking()
            .Include(b => b.Tcc).ThenInclude(t => t!.Aluno)
            .Include(b => b.Tcc).ThenInclude(t => t!.Orientador)
            .Include(b => b.Avaliadores).ThenInclude(a => a.Professor)
            .Include(b => b.Avaliadores).ThenInclude(a => a.MembroExterno)
            .FirstOrDefaultAsync(b => b.Id == idBanca);

        if (banca == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.BancaNaoEncontrada };

        if (banca.NotaFinal == null)
            return new AtaPdfResultado { Status = AtaPdfResultadoStatus.ResultadoNaoRegistrado };

        var avaliadores = banca.Avaliadores
            .Select(a => a.ProfessorId != null
                ? new AtaMembroBancaModel(a.Professor!.Nome, null)
                : new AtaMembroBancaModel(a.MembroExterno!.Nome, a.MembroExterno!.Instituicao))
            .ToList();

        // Motivo de reprovação (Tcc.MotivoRejeicao) só aparece quando a banca de fato
        // reprovou o TCC — derivado do Status já persistido (fonte de verdade histórica),
        // e não recomputado a partir da nota (ver docs/dados/2026-07-13-pdf-ata-questpdf.md, seção 5).
        var motivoReprovacao = banca.Tcc!.Status == StatusTcc.Reprovado
            ? banca.Tcc.MotivoRejeicao
            : null;

        var model = new AtaPdfModel(
            Instituicao: _options.Instituicao,
            Curso: _options.Curso,
            TccTitulo: banca.Tcc.Titulo,
            NomeAluno: banca.Tcc.Aluno!.Nome,
            NomeOrientador: banca.Tcc.Orientador?.Nome ?? "-",
            Avaliadores: avaliadores,
            DataHoraDefesaBrasilia: BrasiliaTimeZoneService.ConverterDeUtcParaBrasilia(banca.DataHora),
            Local: banca.Local,
            NotaFinal: banca.NotaFinal.Value,
            MotivoReprovacao: motivoReprovacao,
            DataGeracaoBrasilia: BrasiliaTimeZoneService.ConverterDeUtcParaBrasilia(DateTime.UtcNow)
        );

        var documento = new AtaPdfDocument(model);
        var pdfBytes = documento.GeneratePdf();

        return new AtaPdfResultado { Status = AtaPdfResultadoStatus.Sucesso, PdfBytes = pdfBytes };
    }
}
