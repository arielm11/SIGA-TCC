using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace TccManager.Api.Services.Pdf;

/// <summary>
/// Layout do PDF final da ata de defesa (RF-02), implementado via <see cref="IDocument"/> do
/// QuestPDF. Não conhece EF Core nem <c>AppDbContext</c> — recebe um <see cref="AtaPdfModel"/>
/// já resolvido pelo <see cref="AtaPdfService"/>. Composição dividida em métodos privados
/// nomeados por seção (cabeçalho, dados do TCC, composição da banca, defesa, resultado,
/// assinaturas, rodapé), conforme docs/arquitetura/2026-07-13-pdf-ata-questpdf.md, seção 2.2.
/// </summary>
public class AtaPdfDocument : IDocument
{
    private readonly AtaPdfModel _model;

    public AtaPdfDocument(AtaPdfModel model)
    {
        _model = model;
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(x => x.FontSize(11));

            page.Header().Element(ComposeCabecalho);
            page.Content().Element(ComposeConteudo);
            page.Footer().Element(ComposeRodape);
        });
    }

    private void ComposeCabecalho(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(2);

            if (!string.IsNullOrWhiteSpace(_model.Instituicao))
                column.Item().AlignCenter().Text(_model.Instituicao).Bold().FontSize(14);

            if (!string.IsNullOrWhiteSpace(_model.Curso))
                column.Item().AlignCenter().Text(_model.Curso).FontSize(11);

            column.Item().AlignCenter().Text("Ata de Defesa de Trabalho de Conclusão de Curso").Bold().FontSize(13);

            if (_model.Rascunho)
            {
                // Identificação visual do rascunho (RNF-05): texto simples, sem marca
                // d'água — decisão técnica fechada em docs/requisitos/2026-07-13-pdf-ata-rascunho-etapa2.md.
                column.Item().PaddingTop(4).AlignCenter()
                    .Text("RASCUNHO — documento preliminar, sujeito a alteração")
                    .Bold().FontSize(10).FontColor(Colors.Red.Darken2);
            }

            column.Item().PaddingTop(8).BorderBottom(1).BorderColor(Colors.Grey.Lighten1);
        });
    }

    private void ComposeConteudo(IContainer container)
    {
        container.PaddingTop(15).Column(column =>
        {
            column.Spacing(14);

            column.Item().Element(ComposeDadosTcc);
            column.Item().Element(ComposeComposicaoBanca);
            column.Item().Element(ComposeDefesa);

            // Rascunho (Etapa 2): sem nota/motivo (RF-01) e sem seção de assinaturas
            // (decisão 7 — omitida por completo, nem em branco).
            if (!_model.Rascunho)
            {
                column.Item().Element(ComposeResultado);
                column.Item().PaddingTop(30).Element(ComposeAssinaturas);
            }
        });
    }

    private void ComposeDadosTcc(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(3);

            column.Item().Text("Dados do Trabalho").Bold().FontSize(12);

            column.Item().Text(text =>
            {
                text.Span("Aluno(a): ").SemiBold();
                text.Span(_model.NomeAluno);
            });

            column.Item().Text(text =>
            {
                text.Span("Título: ").SemiBold();
                text.Span(_model.TccTitulo);
            });
        });
    }

    private void ComposeComposicaoBanca(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(3);

            column.Item().Text("Composição da Banca Examinadora").Bold().FontSize(12);

            column.Item().Text(text =>
            {
                text.Span("Orientador(a): ").SemiBold();
                text.Span(_model.NomeOrientador);
            });

            foreach (var avaliador in _model.Avaliadores)
            {
                column.Item().Text(text =>
                {
                    text.Span("Avaliador(a): ").SemiBold();
                    text.Span(string.IsNullOrWhiteSpace(avaliador.Instituicao)
                        ? avaliador.Nome
                        : $"{avaliador.Nome} ({avaliador.Instituicao})");
                });
            }
        });
    }

    private void ComposeDefesa(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(3);

            column.Item().Text("Data e Local da Defesa").Bold().FontSize(12);

            column.Item().Text(text =>
            {
                text.Span("Data/Hora: ").SemiBold();
                text.Span(_model.DataHoraDefesaBrasilia.ToString("dd/MM/yyyy HH:mm") + " (horário de Brasília)");
            });

            column.Item().Text(text =>
            {
                text.Span("Local: ").SemiBold();
                text.Span(_model.Local);
            });
        });
    }

    private void ComposeResultado(IContainer container)
    {
        container.Column(column =>
        {
            column.Spacing(3);

            column.Item().Text("Resultado").Bold().FontSize(12);

            column.Item().Text(text =>
            {
                text.Span("Nota Final: ").SemiBold();
                // ComposeResultado só é chamado quando !Rascunho (ver ComposeConteudo), e
                // nesse caminho o AtaPdfService só chega a Sucesso com NotaFinal != null.
                text.Span(_model.NotaFinal!.Value.ToString("0.0"));
            });

            if (!string.IsNullOrWhiteSpace(_model.MotivoReprovacao))
            {
                column.Item().PaddingTop(4).Text(text =>
                {
                    text.Span("Motivo da Reprovação: ").SemiBold();
                    text.Span(_model.MotivoReprovacao);
                });
            }
        });
    }

    private void ComposeAssinaturas(IContainer container)
    {
        container.Row(row =>
        {
            row.Spacing(20);

            row.RelativeItem().Column(column =>
            {
                column.Item().PaddingTop(30).BorderTop(1).PaddingTop(3).AlignCenter().Text("Orientador(a)");
            });

            foreach (var _ in _model.Avaliadores)
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().PaddingTop(30).BorderTop(1).PaddingTop(3).AlignCenter().Text("Avaliador(a)");
                });
            }
        });
    }

    private void ComposeRodape(IContainer container)
    {
        container.AlignCenter().Text(text =>
        {
            text.Span("Documento gerado automaticamente pelo sistema SIGA-TCC em ")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
            text.Span(_model.DataGeracaoBrasilia.ToString("dd/MM/yyyy HH:mm") + " (horário de Brasília)")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }
}
