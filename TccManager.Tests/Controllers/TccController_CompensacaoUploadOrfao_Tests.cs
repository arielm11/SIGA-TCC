using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #69, item 4 — compensação de upload órfão em POST /api/tcc/entregas.
///
/// Não há transação entre disco e banco: o arquivo é gravado ANTES do SaveChangesAsync. Se o
/// banco falhar, o controller precisa apagar o arquivo já gravado (CompensarUploadOrfaoAsync),
/// senão sobra lixo permanente em wwwroot/uploads/entregas.
///
/// A falha do banco é injetada por um ISaveChangesInterceptor
/// (<see cref="SaveChangesFalhaEntregaApiFactory"/>) — o InMemory não falha sozinho. Com isso o
/// caminho <c>catch (Exception) => compensa => rethrow</c> fica coberto de verdade, sem mock
/// do controller nem alteração do código de produção.
///
/// O ramo específico <c>catch (DbUpdateException ... SqlException 2601/2627) => 409</c> NÃO é
/// coberto aqui: <c>SqlException</c> não tem construtor público, e o InMemory nunca a produz —
/// ver <c>TccController_EntregaFinalUnica_Tests</c> para o tratamento dessa limitação.
/// </summary>
public class TccController_CompensacaoUploadOrfao_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private static readonly byte[] ConteudoPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nentrega\n%%EOF");

    private static async Task SemearTccAprovadoAsync(SaveChangesFalhaEntregaApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        context.Tccs.Add(new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdOrientador,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        });

        await context.SaveChangesAsync();
    }

    private static MultipartFormDataContent MontarForm(TipoEntrega tipo) => new()
    {
        { new StringContent("Entrega de Teste"), "tituloEntrega" },
        { new StringContent(tipo.ToString()), "tipo" },
        { new ByteArrayContent(ConteudoPdf), "arquivo", "entrega.pdf" }
    };

    [Theory]
    [InlineData(TipoEntrega.Parcial)]
    [InlineData(TipoEntrega.Final)]
    public async Task FalhaAoSalvarNoBanco_RemoveOArquivoJaGravadoEmDisco(TipoEntrega tipo)
    {
        using var factory = new SaveChangesFalhaEntregaApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        // O controller relança depois de compensar; GlobalExceptionHandler (issue #71)
        // intercepta e converte em 500.
        var resposta = await client.PostAsync("/api/tcc/entregas", MontarForm(tipo));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

        // Núcleo do item 4: nenhum arquivo órfão sobra na pasta de uploads.
        Assert.True(
            !Directory.Exists(factory.PastaEntregas) || Directory.GetFiles(factory.PastaEntregas).Length == 0,
            "O arquivo gravado antes da falha de SaveChangesAsync deveria ter sido removido por compensação.");

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Entregas.AnyAsync());
    }

    [Fact]
    public async Task FalhaAoSalvarNoBanco_RespostaEhProblemDetailsGenericoSemDetalheDaExcecao()
    {
        // Issue #71 (middleware de exceção global): o 500 nunca mais carrega a mensagem crua
        // da exceção no corpo, em nenhum ambiente — GlobalExceptionHandler intercepta e
        // devolve um ProblemDetails fixo. Ver GlobalExceptionHandlerTests para a prova, em
        // nível de unidade, de que a exceção original (não uma substituta da compensação)
        // continua chegando ao handler e sendo logada por inteiro no servidor.
        using var factory = new SaveChangesFalhaEntregaApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync("/api/tcc/entregas", MontarForm(TipoEntrega.Parcial));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.Contains("Ocorreu um erro inesperado.", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain(SaveChangesFalhaEntregaApiFactory.MensagemDaFalha, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FalhaAoSalvarNoBanco_CorrelationIdNoHeaderENoCorpoBatem()
    {
        // Achado A09-1 (docs/seguranca/2026-08-19-fix-middleware-excecao-global.md): o
        // ExceptionHandlerMiddleware do framework chama Response.Clear() antes de invocar
        // GlobalExceptionHandler, apagando qualquer header setado antes por
        // CorrelationIdMiddleware. Este teste sobe o pipeline real (não construção manual de
        // HttpContext) para provar que, mesmo assim, o CorrelationId sobrevive e é o MESMO no
        // header X-Correlation-Id e no campo correlationId do corpo.
        using var factory = new SaveChangesFalhaEntregaApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync("/api/tcc/entregas", MontarForm(TipoEntrega.Parcial));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

        Assert.True(resposta.Headers.TryGetValues("X-Correlation-Id", out var valoresHeader));
        var correlationIdHeader = Assert.Single(valoresHeader!);
        Assert.True(Guid.TryParse(correlationIdHeader, out _));

        var corpo = await resposta.Content.ReadAsStringAsync();
        using var documento = JsonDocument.Parse(corpo);
        var correlationIdCorpo = documento.RootElement.GetProperty("correlationId").GetString();

        Assert.Equal(correlationIdHeader, correlationIdCorpo);
    }

    [Fact]
    public async Task UploadBemSucedido_MantemOArquivoEmDisco()
    {
        // Contraprova: sem falha no banco, a compensação não pode disparar. Usa a factory
        // normal (sem o interceptor) para garantir que o assert acima não é vacuously true.
        using var factory = new WebRootIsolatedApiFactory();

        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            context.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                Status = StatusTcc.Aprovado,
                DataCriacao = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync("/api/tcc/entregas", MontarForm(TipoEntrega.Parcial));

        resposta.EnsureSuccessStatusCode();
        Assert.Single(Directory.GetFiles(factory.PastaEntregas));
    }
}
