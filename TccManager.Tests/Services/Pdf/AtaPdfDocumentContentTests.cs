using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using TccManager.Api.Services.Pdf;
using UglyToad.PdfPig;
using Xunit;

namespace TccManager.Tests.Services.Pdf;

/// <summary>
/// Issue #75 ("teste de conteúdo textual/visual do PDF") — os testes existentes de
/// <see cref="AtaPdfDocument"/> (via <c>AtaPdfServiceTests</c>/<c>AtaPdfServiceRascunhoTests</c>)
/// só verificavam a assinatura <c>%PDF</c> e o tamanho em bytes, nunca o texto de fato
/// renderizado — um bug que trocasse o rótulo errado, omitisse um campo, ou vazasse a seção de
/// resultado no rascunho passaria batido. Usa <c>PdfPig</c> (dependência só de teste, não
/// referenciada por nenhum projeto de produção) para extrair o texto real de cada página.
/// </summary>
public class AtaPdfDocumentContentTests
{
    static AtaPdfDocumentContentTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static string ExtrairTexto(byte[] pdfBytes)
    {
        using var documento = PdfDocument.Open(pdfBytes);
        return string.Join("\n", documento.GetPages().Select(p => p.Text));
    }

    private static AtaPdfModel NovoModelo(
        bool rascunho,
        decimal? notaFinal = null,
        string? motivoReprovacao = null,
        IReadOnlyList<AtaMembroBancaModel>? avaliadores = null) => new(
        Instituicao: "Instituto de Teste",
        Curso: "Ciência da Computação",
        TccTitulo: "Um Estudo Sobre Testes Automatizados",
        NomeAluno: "Fulano de Tal",
        NomeOrientador: "Prof. Orientador Exemplo",
        Avaliadores: avaliadores ?? new List<AtaMembroBancaModel>
        {
            new("Prof. Avaliador Interno", null),
            new("Dra. Avaliadora Externa", "Universidade Parceira")
        },
        DataHoraDefesaBrasilia: new DateTime(2026, 6, 15, 14, 30, 0),
        Local: "Auditório Central, Sala 42",
        NotaFinal: notaFinal,
        MotivoReprovacao: motivoReprovacao,
        DataGeracaoBrasilia: new DateTime(2026, 6, 16, 9, 0, 0),
        Rascunho: rascunho
    );

    [Fact]
    public void AtaFinalAprovado_ContemDadosDoTccDaBancaEDoResultado()
    {
        var modelo = NovoModelo(rascunho: false, notaFinal: 87.5m);
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Contains("Ata de Defesa de Trabalho de Conclusão de Curso", texto);
        Assert.Contains("Instituto de Teste", texto);
        Assert.Contains("Ciência da Computação", texto);
        Assert.Contains("Fulano de Tal", texto);
        Assert.Contains("Um Estudo Sobre Testes Automatizados", texto);
        Assert.Contains("Prof. Orientador Exemplo", texto);
        Assert.Contains("Prof. Avaliador Interno", texto);
        Assert.Contains("Dra. Avaliadora Externa (Universidade Parceira)", texto);
        Assert.Contains("Auditório Central, Sala 42", texto);
        Assert.Contains("15/06/2026", texto);
        Assert.Contains("Resultado", texto);
        Assert.Contains("87,5", texto); // ToString("0.0") usa vírgula decimal em pt-BR
        Assert.DoesNotContain("RASCUNHO", texto);
    }

    [Fact]
    public void AtaFinalReprovado_ContemOMotivoDaReprovacao()
    {
        var modelo = NovoModelo(rascunho: false, notaFinal: 45.0m, motivoReprovacao: "Metodologia insuficiente.");
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Contains("Motivo da Reprovação", texto);
        Assert.Contains("Metodologia insuficiente.", texto);
    }

    [Fact]
    public void AtaFinal_ContemUmaLinhaDeAssinaturaPorAvaliadorMaisOOrientador()
    {
        var modelo = NovoModelo(rascunho: false, notaFinal: 90.0m, avaliadores: new List<AtaMembroBancaModel>
        {
            new("Avaliador A", null),
            new("Avaliador B", null),
            new("Avaliador C", null)
        });
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        // 1 "Orientador(a)" da seção de composição + 1 da linha de assinatura = 2 ocorrências;
        // "Avaliador(a)" aparece 1x por avaliador na composição + 1x por avaliador na
        // assinatura = 6 ocorrências (3 avaliadores).
        Assert.Equal(2, ContarOcorrencias(texto, "Orientador(a)"));
        Assert.Equal(6, ContarOcorrencias(texto, "Avaliador(a)"));
    }

    // ───────── Issue #83 (D11): coluna de assinatura do Aluno na ata gerada ─────────

    [Fact]
    public void AtaFinal_ContemAColunaDeAssinaturaDoAluno()
    {
        // O documento oficial não tinha onde o aluno assinar — só orientador e avaliadores.
        // O rótulo é genérico ("Aluno(a)"), não o nome do aluno: as três colunas que já
        // existiam usam rótulo de papel, e o nome já aparece em "Dados do Trabalho".
        //
        // Atenção ao contar: "Aluno(a)" JÁ aparecia 1x em "Dados do Trabalho"
        // ("Aluno(a): Fulano de Tal"), então Assert.Contains sozinho seria verde mesmo sem a
        // mudança desta issue. A contagem (1 do cabeçalho + 1 da assinatura = 2) é o que de
        // fato prova a coluna nova — mesma técnica de
        // AtaFinal_ContemUmaLinhaDeAssinaturaPorAvaliadorMaisOOrientador.
        var modelo = NovoModelo(rascunho: false, notaFinal: 90.0m);
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Contains("Aluno(a)", texto);
        Assert.Equal(2, ContarOcorrencias(texto, "Aluno(a)"));

        // O rótulo da coluna é de PAPEL, não o nome: o nome do aluno aparece uma única vez
        // (no cabeçalho de dados), não repetido na linha de assinaturas. Ver P-01.
        Assert.Equal(1, ContarOcorrencias(texto, "Fulano de Tal"));
    }

    [Fact]
    public void AtaFinal_ColunaDoAlunoVemDepoisDeTodosOsAvaliadores()
    {
        // Fixa a POSIÇÃO (última), não só a presença: a banca decide e assina primeiro, o
        // aluno assina por último, reconhecendo o resultado registrado. Se alguém inserir a
        // coluna antes do loop de avaliadores, este teste acusa.
        var modelo = NovoModelo(rascunho: false, notaFinal: 90.0m, avaliadores: new List<AtaMembroBancaModel>
        {
            new("Avaliador A", null),
            new("Avaliador B", null),
            new("Avaliador C", null)
        });
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        var posicaoAluno = texto.LastIndexOf("Aluno(a)", StringComparison.Ordinal);
        var posicaoUltimoAvaliador = texto.LastIndexOf("Avaliador(a)", StringComparison.Ordinal);
        var posicaoOrientador = texto.LastIndexOf("Orientador(a)", StringComparison.Ordinal);

        Assert.True(posicaoAluno > posicaoUltimoAvaliador,
            "A coluna 'Aluno(a)' deve vir depois de todos os avaliadores na linha de assinaturas.");
        Assert.True(posicaoAluno > posicaoOrientador,
            "A coluna 'Aluno(a)' deve vir depois da coluna do orientador.");
    }

    [Fact]
    public void AtaFinal_NaoDuplicaAColunaDoAlunoPorAvaliador()
    {
        // A coluna nova está FORA do foreach de avaliadores: aparece exatamente uma vez,
        // independentemente do tamanho da banca.
        var modelo = NovoModelo(rascunho: false, notaFinal: 90.0m, avaliadores: new List<AtaMembroBancaModel>
        {
            new("Avaliador A", null),
            new("Avaliador B", null),
            new("Avaliador C", null),
            new("Avaliador D", null)
        });
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        // 1 ocorrência em "Dados do Trabalho" + 1 na linha de assinaturas, com 4 avaliadores.
        Assert.Equal(2, ContarOcorrencias(texto, "Aluno(a)"));
    }

    [Fact]
    public void AtaRascunho_NaoContemAColunaDeAssinaturaDoAluno()
    {
        // A guarda !Rascunho de ComposeConteudo não foi tocada por #83: o rascunho continua
        // sem NENHUMA seção de assinatura, inclusive a nova. Sobra só a ocorrência de
        // "Aluno(a): Fulano de Tal" no cabeçalho de dados.
        var modelo = NovoModelo(rascunho: true);
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Equal(1, ContarOcorrencias(texto, "Aluno(a)"));
    }

    [Fact]
    public void AtaRascunho_NaoContemSecaoDeResultadoNemAssinaturas()
    {
        // Decisão de negócio (RF-01/Etapa 2): o rascunho omite resultado e assinaturas por
        // completo (nem em branco) — se algum dia essa seção vazar para o rascunho, é uma
        // regressão de RNF-05 (o avaliador externo não pode ver a nota antes de todos
        // registrarem).
        var modelo = NovoModelo(rascunho: true);
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.DoesNotContain("Resultado", texto);
        Assert.DoesNotContain("Nota Final", texto);
        Assert.DoesNotContain("Assinatura", texto);
        Assert.DoesNotContain("Orientador(a)\n", texto); // linha de assinatura isolada
    }

    [Fact]
    public void AtaRascunho_ExibeOAvisoDeRascunhoEOsDadosBasicos()
    {
        var modelo = NovoModelo(rascunho: true);
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Contains("RASCUNHO", texto);
        Assert.Contains("documento preliminar", texto);
        Assert.Contains("Fulano de Tal", texto);
        Assert.Contains("Um Estudo Sobre Testes Automatizados", texto);
    }

    [Fact]
    public void AtaSemOrientadorDesignado_ExibeTracoNoLugarDoNome()
    {
        // AtaPdfService.MontarModel usa "tcc.Orientador?.Nome ?? "-"" quando OrientadorId é
        // nulo — este teste prova que esse "-" chega íntegro ao texto do PDF.
        var modelo = NovoModelo(rascunho: true) with { NomeOrientador = "-" };
        var pdfBytes = new AtaPdfDocument(modelo).GeneratePdf();

        var texto = ExtrairTexto(pdfBytes);

        Assert.Contains("Orientador(a): -", texto.Replace("\n", " "));
    }

    private static int ContarOcorrencias(string texto, string trecho)
    {
        var contagem = 0;
        var indice = 0;
        while ((indice = texto.IndexOf(trecho, indice, StringComparison.Ordinal)) != -1)
        {
            contagem++;
            indice += trecho.Length;
        }
        return contagem;
    }
}
