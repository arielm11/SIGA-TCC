using System.Net;
using System.Text;
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

        // O controller relança depois de compensar; o host de teste roda em Development, onde
        // o DeveloperExceptionPageMiddleware converte a exceção em 500 (não há UseExceptionHandler).
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
    public async Task FalhaAoSalvarNoBanco_NaoMascaraOErroOriginal()
    {
        // A compensação é best-effort e nunca pode substituir/engolir a exceção que a causou:
        // o erro que sobe é o do banco, não um erro de I/O da limpeza.
        using var factory = new SaveChangesFalhaEntregaApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync("/api/tcc/entregas", MontarForm(TipoEntrega.Parcial));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Contains(
            SaveChangesFalhaEntregaApiFactory.MensagemDaFalha,
            await resposta.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
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
