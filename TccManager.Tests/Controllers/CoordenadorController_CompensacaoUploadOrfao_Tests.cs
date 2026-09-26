using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #105 — compensação de upload órfão em
/// POST /api/coordenador/banca/{idBanca}/registrar-resultado.
///
/// Não há transação entre disco e banco: o arquivo da ata é gravado ANTES do
/// SaveChangesAsync. Se o banco falhar, o controller precisa apagar o arquivo já gravado
/// (CompensarUploadOrfaoAsync, extraído para CompensacaoUploadOrfao e compartilhado com
/// TccController.EnviarEntrega), senão sobra lixo permanente em wwwroot/uploads/atas — mesma
/// mecânica de <see cref="TccController_CompensacaoUploadOrfao_Tests"/>, agora para a ata.
/// </summary>
public class CoordenadorController_CompensacaoUploadOrfao_Tests
{
    private const int IdCoordenador = 1;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    private static readonly byte[] ConteudoPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nata\n%%EOF");

    private static async Task<(int bancaId, int tccId)> SemearBancaPendenteAsync(SaveChangesFalhaBancaApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor", Email = "professor@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdProfessor,
            Status = StatusTcc.AguardandoDefesa,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala de Teste" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        return (banca.Id, tcc.Id);
    }

    private static MultipartFormDataContent MontarForm(decimal nota = 85.0m)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(nota.ToString(System.Globalization.CultureInfo.InvariantCulture)), "notaFinal" }
        };

        var arquivo = new ByteArrayContent(ConteudoPdf);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(arquivo, "arquivoAta", "ata.pdf");

        return form;
    }

    [Fact]
    public async Task FalhaAoSalvarNoBanco_RemoveOArquivoDaAtaJaGravadoEmDisco()
    {
        using var factory = new SaveChangesFalhaBancaApiFactory();
        var (bancaId, tccId) = await SemearBancaPendenteAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        // O controller relança depois de compensar; GlobalExceptionHandler (issue #71)
        // intercepta e converte em 500.
        var resposta = await client.PostAsync($"/api/coordenador/banca/{bancaId}/registrar-resultado", MontarForm());

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

        // Núcleo do achado: nenhum arquivo órfão sobra na pasta de uploads/atas.
        Assert.True(
            !Directory.Exists(factory.PastaAtas) || Directory.GetFiles(factory.PastaAtas).Length == 0,
            "O arquivo da ata gravado antes da falha de SaveChangesAsync deveria ter sido removido por compensação.");

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FindAsync(tccId);
        Assert.Equal(StatusTcc.AguardandoDefesa, tcc!.Status); // nao avancou para Finalizado/Reprovado
        var banca = await context.Banca.FindAsync(bancaId);
        Assert.Null(banca!.NotaFinal);
        Assert.Equal(string.Empty, banca.AtaCaminho);
    }

    [Fact]
    public async Task UploadBemSucedido_MantemOArquivoDaAtaEmDisco()
    {
        // Contraprova: sem falha no banco, a compensação não pode disparar.
        using var factory = new WebRootIsolatedApiFactory();

        int bancaId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = IdProfessor, Nome = "Professor", Email = "professor@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

            var tcc = new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorId = IdProfessor,
                Status = StatusTcc.AguardandoDefesa,
                DataCriacao = DateTime.UtcNow
            };
            context.Tccs.Add(tcc);
            await context.SaveChangesAsync();

            var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala de Teste" };
            context.Banca.Add(banca);
            await context.SaveChangesAsync();
            bancaId = banca.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var resposta = await client.PostAsync($"/api/coordenador/banca/{bancaId}/registrar-resultado", MontarForm());

        resposta.EnsureSuccessStatusCode();
        Assert.Single(Directory.GetFiles(factory.PastaAtas));
    }
}
