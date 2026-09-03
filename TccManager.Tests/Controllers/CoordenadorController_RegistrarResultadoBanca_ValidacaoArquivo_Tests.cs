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
/// Issue #83 (D6) — validação de conteúdo do <c>arquivoAta</c> em
/// POST /api/coordenador/banca/{idBanca}/registrar-resultado.
///
/// Antes desta issue este endpoint aceitava QUALQUER coisa como "ata assinada": não havia
/// whitelist de extensão (diferente de <c>TccController.EnviarEntrega</c>, que já tinha
/// quatro) nem validação de magic bytes. Um .exe renomeado para .pdf — ou um .txt com o nome
/// certo — era gravado no storage e ficava referenciado por <c>Banca.AtaCaminho</c>.
///
/// Molde de asserção herdado de
/// <see cref="TccController_EnviarEntrega_MagicBytes_Tests.AssertRejeitadaPorConteudoAsync"/>:
/// além do 400, o teste prova que nada foi persistido — nem <c>Banca.NotaFinal</c>, nem
/// transição de <c>Tcc.Status</c>, nem arquivo em <c>factory.PastaAtas</c> (a validação roda
/// ANTES do <c>UploadAsync</c>, e é isso que evita arquivo órfão em disco).
/// </summary>
public class CoordenadorController_RegistrarResultadoBanca_ValidacaoArquivo_Tests
{
    private const int IdCoordenador = 1;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    // MZ + ruído: um .exe real renomeado para .pdf.
    private static readonly byte[] ConteudoExecutavel =
        { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 };

    private static readonly byte[] AssinaturaZip = { 0x50, 0x4B, 0x03, 0x04 };

    private static async Task<(WebRootIsolatedApiFactory factory, int bancaId, int tccId)> PrepararBancaPendenteAsync()
    {
        var factory = new WebRootIsolatedApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor Teste", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

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

        return (factory, banca.Id, tcc.Id);
    }

    private static MultipartFormDataContent MontarForm(string nomeArquivo, byte[] conteudo, decimal nota = 85.0m)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(nota.ToString(System.Globalization.CultureInfo.InvariantCulture)), "notaFinal" }
        };

        var arquivo = new ByteArrayContent(conteudo);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(arquivo, "arquivoAta", nomeArquivo);

        return form;
    }

    /// <summary>
    /// Rejeição por CONTEÚDO: 400 com a mensagem de PDF inválido e nenhum efeito colateral —
    /// nem no banco, nem no storage.
    /// </summary>
    private static async Task AssertRejeitadaPorConteudoAsync(
        WebRootIsolatedApiFactory factory, int tccId, int bancaId, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "não é um PDF válido",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await AssertNadaFoiPersistidoAsync(factory, tccId, bancaId);
    }

    private static async Task AssertNadaFoiPersistidoAsync(
        WebRootIsolatedApiFactory factory, int tccId, int bancaId)
    {
        using var context = factory.CriarContextoDireto();

        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);
        var banca = await context.Banca.FirstAsync(b => b.Id == bancaId);

        Assert.Equal(StatusTcc.AguardandoDefesa, tcc.Status);
        Assert.Null(banca.NotaFinal);
        Assert.Equal(string.Empty, banca.AtaCaminho);

        Assert.True(
            !Directory.Exists(factory.PastaAtas) || Directory.GetFiles(factory.PastaAtas).Length == 0,
            "Nenhum arquivo deveria ter sido gravado quando o upload é rejeitado.");
    }

    // ───────────────────── Whitelist de extensão ─────────────────────

    [Theory]
    [InlineData("ata.txt")]
    [InlineData("ata.exe")]
    [InlineData("ata.docx")]
    [InlineData("ata.zip")]
    [InlineData("ata")]          // sem extensão nenhuma
    [InlineData("ata.pdf.exe")]  // dupla extensão: vale a última
    public async Task ExtensaoForaDaWhitelist_Retorna400ENaoPersiste(string nomeArquivo)
    {
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        // Conteúdo PDF VÁLIDO de propósito: o que reprova é só o nome do arquivo.
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm(nomeArquivo, ConteudoArquivoTeste.AssinaturaPdf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "deve ser um arquivo PDF",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await AssertNadaFoiPersistidoAsync(factory, tccId, bancaId);
    }

    [Fact]
    public async Task ExtensaoNaoPermitidaComConteudoValido_ContinuaBloqueadaPelaExtensao()
    {
        // Fixa a ORDEM das validações (extensão antes de conteúdo): um PDF íntegro chamado
        // .exe é barrado pela mensagem de formato, nunca pela de conteúdo. Espelha
        // TccController_EnviarEntrega_MagicBytes_Tests.ExtensaoNaoPermitidaComConteudoValido_*.
        var (factory, bancaId, _) = await PrepararBancaPendenteAsync();
        using var _fd = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.exe", ConteudoArquivoTeste.PdfComCorpo("pdf-de-verdade")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("deve ser um arquivo PDF", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("não é um PDF válido", corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ata.PDF")]
    [InlineData("ata.Pdf")]
    public async Task ExtensaoPdfEmMaiusculas_EhAceita(string nomeArquivo)
    {
        // ToLowerInvariant() na extensão: o Windows/Word entrega ".PDF" com frequência, e
        // rejeitar isso seria um falso positivo caro para o Coordenador.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm(nomeArquivo, ConteudoArquivoTeste.AssinaturaPdf));

        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        Assert.Equal(StatusTcc.Finalizado, (await context.Tccs.FirstAsync(t => t.Id == tccId)).Status);
    }

    // ───────────────────── Magic bytes ─────────────────────

    [Fact]
    public async Task ExecutavelRenomeadoParaPdf_Retorna400ENaoPersiste()
    {
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", ConteudoExecutavel));

        await AssertRejeitadaPorConteudoAsync(factory, tccId, bancaId, response);
    }

    [Fact]
    public async Task TextoPuroComNomePdf_Retorna400ENaoPersiste()
    {
        // Caso mais comum na prática: alguém renomeia um .txt para .pdf.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", Encoding.ASCII.GetBytes("isto e apenas texto, nao um PDF")));

        await AssertRejeitadaPorConteudoAsync(factory, tccId, bancaId, response);
    }

    [Fact]
    public async Task AssinaturaPdfTruncada_SemOHifen_Retorna400()
    {
        // Exatamente o literal de 4 bytes que a suíte inteira usava antes desta issue: a
        // assinatura exigida tem 5 bytes ("%PDF-"). Este teste é a documentação executável
        // do porquê ~15 testes existentes precisaram ser corrigidos.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", ConteudoArquivoTeste.AssinaturaPdfTruncada));

        await AssertRejeitadaPorConteudoAsync(factory, tccId, bancaId, response);
    }

    [Fact]
    public async Task AssinaturaDeZipComNomePdf_Retorna400()
    {
        // Assinatura válida, mas de OUTRO tipo: a validação é por extensão declarada.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", AssinaturaZip));

        await AssertRejeitadaPorConteudoAsync(factory, tccId, bancaId, response);
    }

    [Fact]
    public async Task ConteudoInvalidoEmReprovacao_NaoGravaMotivoNemArquivo()
    {
        // O ramo de reprovação sanitiza/valida o motivo ANTES da checagem de conteúdo; este
        // teste garante que um conteúdo inválido ainda assim não deixa nada persistido no
        // caminho mais longo do método.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var form = MontarForm("ata.pdf", ConteudoExecutavel, nota: 40.0m);
        form.Add(new StringContent("Metodologia insuficiente."), "motivoReprovacao");

        var response = await client.PostAsync($"/api/coordenador/banca/{bancaId}/registrar-resultado", form);

        await AssertRejeitadaPorConteudoAsync(factory, tccId, bancaId, response);

        using var context = factory.CriarContextoDireto();
        Assert.Null((await context.Tccs.FirstAsync(t => t.Id == tccId)).MotivoRejeicao);
    }

    // ───────────────────── Aceitação ─────────────────────

    [Fact]
    public async Task PdfValidoComCorpo_Retorna200EGravaBytesIntegros()
    {
        // O mesmo stream lido para validar o cabeçalho é reposicionado e enviado ao storage:
        // o arquivo gravado precisa conter TODO o conteúdo, não só o que sobrou depois dos
        // 5 bytes do cabeçalho.
        var (factory, bancaId, tccId) = await PrepararBancaPendenteAsync();
        using var _ = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var conteudo = ConteudoArquivoTeste.PdfComCorpo("corpo da ata com varios bytes");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", conteudo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var arquivos = Directory.GetFiles(factory.PastaAtas);
        Assert.Single(arquivos);
        Assert.Equal(conteudo, await File.ReadAllBytesAsync(arquivos[0]));

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);
        var banca = await context.Banca.FirstAsync(b => b.Id == bancaId);

        Assert.Equal(StatusTcc.Finalizado, tcc.Status);
        Assert.Equal(85.0m, banca.NotaFinal);
        Assert.NotEqual(string.Empty, banca.AtaCaminho);
    }

    [Fact]
    public async Task AssinaturaPdfMinimaDe5Bytes_EhAceita()
    {
        // Fronteira inferior: exatamente a assinatura, sem nenhum byte de corpo.
        var (factory, bancaId, _) = await PrepararBancaPendenteAsync();
        using var _fd = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", ConteudoArquivoTeste.AssinaturaPdf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(Directory.GetFiles(factory.PastaAtas));
    }

    [Fact]
    public async Task ConteudoInvalido_EmBancaJaRegistrada_ContinuaBloqueadoPelaRegraDeNegocio()
    {
        // A validação de conteúdo não pode "vazar" à frente das regras de negócio: uma banca
        // com resultado já lançado é barrada antes, com a mensagem própria.
        var (factory, bancaId, _) = await PrepararBancaPendenteAsync();
        using var _fd = factory;
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var primeiro = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", ConteudoArquivoTeste.AssinaturaPdf));
        primeiro.EnsureSuccessStatusCode();

        var segundo = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarForm("ata.pdf", ConteudoExecutavel));

        Assert.Equal(HttpStatusCode.BadRequest, segundo.StatusCode);
        var corpo = await segundo.Content.ReadAsStringAsync();
        Assert.Contains("já foi registrado", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("não é um PDF válido", corpo, StringComparison.Ordinal);
    }
}
