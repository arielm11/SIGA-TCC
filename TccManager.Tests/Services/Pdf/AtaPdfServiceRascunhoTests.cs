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
/// Testes de unidade do fluxo RASCUNHO do <see cref="AtaPdfService"/> (N2 Etapa 2):
/// <c>GerarAtaRascunhoAsync</c> resolve BancaNaoEncontrada (404), ResultadoJaRegistrado
/// (410, quando NotaFinal != null) e Sucesso (PDF %PDF) enquanto NotaFinal == null,
/// incluindo a composição com avaliador interno + externo. A verificação de que o PDF
/// rascunho OMITE nota/assinaturas não é feita por parsing de bytes (PDF QuestPDF é
/// comprimido — mesmo motivo pelo qual os testes da Etapa 1 só validam a assinatura %PDF);
/// a omissão condicional é coberta indiretamente pela lógica de AtaPdfDocument e sinalizada
/// como não coberta por asserção de conteúdo ao qa-agent.
/// </summary>
public class AtaPdfServiceRascunhoTests
{
    static AtaPdfServiceRascunhoTests()
    {
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
        bool comAvaliadorExterno = false)
    {
        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        var avaliadorInterno = new Usuario { Nome = "Avaliador Interno", Email = "aval@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador, avaliadorInterno);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC Rascunho",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = statusTcc,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(2), Local = "Sala 1", NotaFinal = notaFinal };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = avaliadorInterno.Id });

        if (comAvaliadorExterno)
        {
            var externo = new MembroExterno { Nome = "Externa", Email = "externa@outra.edu", Instituicao = "Universidade Externa" };
            context.MembrosExternos.Add(externo);
            await context.SaveChangesAsync();
            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = externo.Id });
        }

        await context.SaveChangesAsync();
        return banca.Id;
    }

    private static void AssertAssinaturaPdf(byte[] bytes)
    {
        Assert.True(bytes.Length > 100, "PDF rascunho é suspeito de estar vazio/truncado.");
        Assert.True(
            bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46,
            "Os bytes gerados não começam com a assinatura %PDF.");
    }

    [Fact]
    public async Task GerarAtaRascunho_BancaInexistente_RetornaBancaNaoEncontrada()
    {
        using var context = NovoContexto();
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaRascunhoAsync(999);

        Assert.Equal(AtaPdfResultadoStatus.BancaNaoEncontrada, resultado.Status);
        Assert.Null(resultado.PdfBytes);
    }

    [Fact]
    public async Task GerarAtaRascunho_ResultadoJaRegistrado_Retorna410_SemPdf()
    {
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(context, notaFinal: 90m, statusTcc: StatusTcc.Finalizado);
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaRascunhoAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.ResultadoJaRegistrado, resultado.Status);
        Assert.Null(resultado.PdfBytes);
    }

    [Fact]
    public async Task GerarAtaRascunho_SemNota_GeraPdf()
    {
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(context, notaFinal: null, statusTcc: StatusTcc.AguardandoDefesa);
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaRascunhoAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.Sucesso, resultado.Status);
        Assert.NotNull(resultado.PdfBytes);
        AssertAssinaturaPdf(resultado.PdfBytes!);
    }

    [Fact]
    public async Task GerarAtaRascunho_SemNota_ComAvaliadorInternoEExterno_GeraPdf()
    {
        // Exercita os dois ramos de resolução do avaliador (Professor + MembroExterno) no
        // rascunho — um mapeamento incorreto lançaria NRE antes de gerar o PDF.
        using var context = NovoContexto();
        var bancaId = await SemearBancaAsync(
            context, notaFinal: null, statusTcc: StatusTcc.AguardandoDefesa, comAvaliadorExterno: true);
        var servico = NovoServico(context);

        var resultado = await servico.GerarAtaRascunhoAsync(bancaId);

        Assert.Equal(AtaPdfResultadoStatus.Sucesso, resultado.Status);
        AssertAssinaturaPdf(resultado.PdfBytes!);
    }
}
