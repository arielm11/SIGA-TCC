using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #145 (achado B1): RegistrarResultadoBanca já era protegido pelo RowVersion de Tcc
/// (#113, banca.Tcc.Status é alterado no mesmo SaveChanges), mas a DbUpdateConcurrencyException
/// caía no catch(Exception) genérico e virava 500. Agora é 409.
/// </summary>
public class CoordenadorController_RegistrarResultadoBanca_Concorrencia_Tests
{
    private const int IdCoordenador = 1;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    [Fact]
    public async Task ConflitoDeConcorrencia_Retorna409ENaoDeixaArquivoOrfao()
    {
        var factory = new SaveChangesFalhaConcorrenciaBancaApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC", Resumo = "Resumo", AlunoId = IdAluno, OrientadorId = IdProfessor,
            Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("80"), "notaFinal");
        var pdfFake = new ByteArrayContent(ConteudoArquivoTeste.AssinaturaPdf);
        pdfFake.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdfFake, "arquivoAta", "ata.pdf");

        var response = await client.PostAsync($"/api/coordenador/banca/{banca.Id}/registrar-resultado", form);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // O arquivo gravado em disco antes do SaveChanges falhar não deve ficar órfão.
        var arquivosEmDisco = Directory.Exists(factory.PastaAtas) ? Directory.GetFiles(factory.PastaAtas) : Array.Empty<string>();
        Assert.Empty(arquivosEmDisco);
    }
}
