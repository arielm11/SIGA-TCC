using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Infrastructure;
using TccManager.Api.Data;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Services.Pdf;

/// <summary>
/// Testes de unidade do <see cref="AtaPdfService"/> exercitando a lógica de orquestração
/// diretamente sobre um <see cref="AppDbContext"/> InMemory, sem o pipeline HTTP. O foco é a
/// resolução de status (banca inexistente/ resultado não registrado/ sucesso) e a resolução
/// do polimorfismo do avaliador (Professor interno vs. MembroExterno), que só é exercida de
/// ponta a ponta quando o PDF chega a ser gerado — se o mapeamento escolhesse o ramo errado,
/// o acesso a <c>Professor!</c> / <c>MembroExterno!</c> lançaria NullReferenceException aqui.
/// </summary>
public class AtaPdfServiceTests
{
    static AtaPdfServiceTests()
    {
        // Em teste de unidade não passamos pelo PdfSetup.AddAtaPdf (que configura a licença);
        // sem isso o GeneratePdf() do QuestPDF lançaria por falta de licença aceita.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static AppDbContext NovoContexto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static AtaPdfService NovoServico(AppDbContext context)
    {
        var options = Options.Create(new AtaInstitucionalOptions
        {
            Instituicao = "Instituto de Teste",
            Curso = "Ciência da Computação"
        });
        return new AtaPdfService(context, options);
    }

    private static async Task<int> SemearBancaAsync(
        AppDbContext context,
        decimal? notaFinal,
        StatusTcc statusTcc,
        string? motivoRejeicao = null,
        bool comAvaliadorExterno = false)
    {
        var aluno = new Usuario { Nome = "Aluno da Silva", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Prof. Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliadorInterno = new Usuario { Nome = "Prof. Avaliador Interno", Email = "aval@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador, avaliadorInterno);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "Um Título de TCC",
            Resumo = "Resumo",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = statusTcc,
            MotivoRejeicao = motivoRejeicao,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca
        {
            TccId = tcc.Id,
            DataHora = DateTime.UtcNow.AddDays(-1),
            Local = "Auditório 1",
            NotaFinal = notaFinal
        };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = avaliadorInterno.Id });

        if (comAvaliadorExterno)
        {
            var externo = new MembroExterno { Nome = "Dra. Externa", Email = "externa@outra.edu", Instituicao = "Universidade Externa" };
            context.MembrosExternos.Add(externo);
            await context.SaveChangesAsync();
            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = externo.Id });
        }

        await context.SaveChangesAsync();
        return banca.Id;
    }

    [Fact]
    public async Task GerarAtaFinal_BancaInexistente_RetornaBancaNaoEncontrada_SemPdf()
    {
        using var context = NovoContexto();
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaFinalAsync(999);

        Assert.Equal(AtaPdfResultadoStatus.BancaNaoEncontrada, resultado.Status);
        Assert.Null(resultado.PdfBytes);
    }

    [Fact]
    public async Task GerarAtaFinal_SemNotaFinal_RetornaResultadoNaoRegistrado_SemPdf()
    {
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(context, notaFinal: null, statusTcc: StatusTcc.AguardandoDefesa);
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaFinalAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.ResultadoNaoRegistrado, resultado.Status);
        Assert.Null(resultado.PdfBytes);
    }

    [Fact]
    public async Task GerarAtaFinal_Aprovado_ComAvaliadorInternoEExterno_GeraPdf()
    {
        // Exercita os DOIS ramos da resolução do avaliador (Professor + MembroExterno)
        // na mesma banca; um mapeamento incorreto lançaria NRE antes de gerar o PDF.
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(
            context, notaFinal: 85.0m, statusTcc: StatusTcc.Finalizado, comAvaliadorExterno: true);
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaFinalAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.Sucesso, resultado.Status);
        Assert.NotNull(resultado.PdfBytes);
        AssertAssinaturaPdf(resultado.PdfBytes!);
    }

    [Fact]
    public async Task GerarAtaFinal_Reprovado_ComMotivo_GeraPdf()
    {
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(
            context, notaFinal: 40.0m, statusTcc: StatusTcc.Reprovado,
            motivoRejeicao: "Não atingiu os requisitos mínimos.");
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaFinalAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.Sucesso, resultado.Status);
        Assert.NotNull(resultado.PdfBytes);
        AssertAssinaturaPdf(resultado.PdfBytes!);
    }

    private static void AssertAssinaturaPdf(byte[] bytes)
    {
        Assert.True(bytes.Length > 100, "PDF gerado é suspeito de estar vazio/truncado.");
        Assert.True(
            bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46,
            "Os bytes gerados não começam com a assinatura %PDF.");
    }
}
