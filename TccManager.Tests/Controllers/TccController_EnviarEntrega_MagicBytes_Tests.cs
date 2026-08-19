using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #69, item 2 — validação de magic bytes em POST /api/tcc/entregas.
///
/// A checagem de extensão (RF5, já coberta em <see cref="TccController_EnviarEntrega_Tests"/>)
/// confia apenas no nome do arquivo. Aqui o alvo é o CONTEÚDO: um executável renomeado para
/// .pdf, um .docx que não é um contêiner ZIP e um .doc sem cabeçalho OLE devem ser rejeitados
/// com 400 e não podem gerar nem registro no banco nem arquivo em disco.
///
/// Os casos de sucesso por extensão (.pdf/.doc/.docx/.zip com assinatura correta) já estão
/// cobertos em <c>TccController_EnviarEntrega_Tests.RF5_ExtensaoPermitida_DeveRetornarOk</c>,
/// que passou a montar magic numbers coerentes — aqui só se acrescenta o que aquele arquivo
/// não cobre: a rejeição por conteúdo e as bordas de cabeçalho truncado/tipo trocado.
/// </summary>
public class TccController_EnviarEntrega_MagicBytes_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private static readonly byte[] AssinaturaPdf = { 0x25, 0x50, 0x44, 0x46, 0x2D };
    private static readonly byte[] AssinaturaZip = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] AssinaturaOle = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    // MZ + ruído: um .exe real renomeado para .pdf/.docx/.doc.
    private static readonly byte[] ConteudoExecutavel =
        { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };

    private static async Task<int> SemearTccAprovadoAsync(WebRootIsolatedApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdOrientador,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };

        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        return tcc.Id;
    }

    private static MultipartFormDataContent MontarForm(string nomeArquivo, byte[] conteudo, TipoEntrega tipo = TipoEntrega.Parcial)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Entrega de Teste"), "tituloEntrega" },
            { new StringContent(tipo.ToString()), "tipo" },
            { new ByteArrayContent(conteudo), "arquivo", nomeArquivo }
        };

        return form;
    }

    private static async Task AssertRejeitadaPorConteudoAsync(
        WebRootIsolatedApiFactory factory,
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "Conteúdo do arquivo não corresponde à extensão informada.",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Entregas.AnyAsync());

        // A validação acontece ANTES do UploadAsync: nada pode ter sido gravado em disco.
        Assert.True(
            !Directory.Exists(factory.PastaEntregas) || Directory.GetFiles(factory.PastaEntregas).Length == 0,
            "Nenhum arquivo deveria ter sido gravado quando o conteúdo é rejeitado.");
    }

    // ───────────────────── .pdf com conteúdo que não é PDF ─────────────────────

    [Fact]
    public async Task Pdf_ComConteudoDeExecutavelRenomeado_Retorna400ENaoPersiste()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("malware.pdf", ConteudoExecutavel));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Pdf_ComBytesAleatorios_Retorna400()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var aleatorios = new byte[64];
        Random.Shared.NextBytes(aleatorios);
        aleatorios[0] = 0x00; // garante que não colide com "%" por acaso

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.pdf", aleatorios));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Pdf_ComTextoPuro_Retorna400()
    {
        // Caso mais comum na prática: alguém renomeia um .txt para .pdf.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarForm("entrega.pdf", Encoding.ASCII.GetBytes("isto e apenas texto, nao um PDF")));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Pdf_ComCabecalhoTruncado_Retorna400()
    {
        // "%PDF" sem o hífen final: menor que a assinatura completa exigida ("%PDF-").
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarForm("entrega.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Pdf_ComAssinaturaDeZip_Retorna400()
    {
        // Assinatura válida, mas de OUTRO tipo: a validação é por extensão declarada.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.pdf", AssinaturaZip));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    // ───────────────────── .docx / .zip sem assinatura ZIP ─────────────────────

    [Theory]
    [InlineData("entrega.docx")]
    [InlineData("entrega.zip")]
    public async Task ContainerZip_SemAssinaturaPk_Retorna400(string nomeArquivo)
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm(nomeArquivo, ConteudoExecutavel));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Theory]
    [InlineData("entrega.docx")]
    [InlineData("entrega.zip")]
    public async Task ContainerZip_ComAssinaturaDePdf_Retorna400(string nomeArquivo)
    {
        // Regressão do bug corrigido no harness antigo: antes da issue #69 o teste enviava
        // "%PDF" para toda extensão. Um PDF renomeado para .docx deve ser rejeitado.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm(nomeArquivo, AssinaturaPdf));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    // ───────────────────── .doc sem assinatura OLE ─────────────────────

    [Fact]
    public async Task Doc_SemAssinaturaOle_Retorna400()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.doc", ConteudoExecutavel));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Doc_ComAssinaturaOleTruncada_Retorna400()
    {
        // A assinatura OLE tem 8 bytes; 4 bytes não bastam.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarForm("entrega.doc", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    [Fact]
    public async Task Doc_ComAssinaturaDeZip_Retorna400()
    {
        // .docx e .doc são formatos distintos: o contêiner ZIP não vale para o .doc legado.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.doc", AssinaturaZip));

        await AssertRejeitadaPorConteudoAsync(factory, response);
    }

    // ───────────────────── Aceitações e ordem das validações ─────────────────────

    [Fact]
    public async Task Pdf_ComAssinaturaValidaEConteudoAdicional_Retorna200EGravaBytesIntegros()
    {
        // Complementa o caso de sucesso existente: o mesmo stream lido para validar o
        // cabeçalho é reposicionado e enviado ao storage — o arquivo gravado precisa
        // conter TODO o conteúdo, não apenas o que sobrou depois do cabeçalho.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var conteudo = Encoding.ASCII.GetBytes("%PDF-1.7\ncorpo do documento com varios bytes\n%%EOF");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.pdf", conteudo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var arquivos = Directory.GetFiles(factory.PastaEntregas);
        Assert.Single(arquivos);
        Assert.Equal(conteudo, await File.ReadAllBytesAsync(arquivos[0]));
    }

    [Fact]
    public async Task Doc_ComAssinaturaOleValida_Retorna200()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var conteudo = AssinaturaOle.Concat(Encoding.ASCII.GetBytes("corpo do doc")).ToArray();

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.doc", conteudo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ExtensaoNaoPermitidaComConteudoValido_ContinuaBloqueadaPelaExtensao()
    {
        // Ordem das validações: a extensão é checada antes do conteúdo. Um PDF íntegro
        // chamado .exe segue barrado pela mensagem de formato, não pela de conteúdo.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.exe", AssinaturaPdf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("Formato de arquivo não permitido", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Conteúdo do arquivo", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConteudoInvalido_EmTccNaoAprovado_ContinuaBloqueadoPelaRegraDeNegocio()
    {
        // A validação de conteúdo não pode "vazar" à frente das regras de negócio:
        // um TCC não aprovado é barrado antes, com a mensagem própria.
        using var factory = new WebRootIsolatedApiFactory();

        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            context.Tccs.Add(new Tcc
            {
                Titulo = "TCC Pendente",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.PostAsync("/api/tcc/entregas", MontarForm("entrega.pdf", ConteudoExecutavel));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("aprovado", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
