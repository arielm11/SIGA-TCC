using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #83 — GET /api/coordenador/banca/{idBanca}/ata-assinada.
///
/// O coração da issue: o arquivo que o Coordenador anexa em RegistrarResultadoBanca
/// (<c>Banca.AtaCaminho</c>) era gravado em disco e nunca mais podia ser recuperado por
/// nenhuma tela ou rota — o único download existente (<c>ata-pdf</c>) devolve o PDF GERADO
/// pelo QuestPDF, um documento diferente. Este endpoint serve o arquivo bruto.
///
/// Estrutura espelhada de <see cref="TccController_DownloadEntrega_Tests"/>, que já fixou o
/// padrão de "download autenticado de arquivo de storage": arquivo físico sempre no web root
/// temporário da <see cref="WebRootIsolatedApiFactory"/>, nunca no wwwroot real do projeto.
/// </summary>
public class CoordenadorController_AtaAssinada_Tests
{
    private const int IdCoordenador = 1;
    private const int IdAluno = 10;
    private const int IdProfessor = 20;

    /// <summary>
    /// Semeia aluno + orientador + TCC + banca. O <paramref name="ataCaminho"/> é gravado
    /// literalmente em <c>Banca.AtaCaminho</c> (null = deixa o default string.Empty, que é o
    /// estado real de "nenhuma cópia anexada" — a coluna é NOT NULL, ver seção 4.3 da
    /// arquitetura).
    /// </summary>
    /// <param name="comAvaliador">
    /// Só necessário para os testes que também chamam <c>ata-pdf</c>: o AtaPdfService trata
    /// banca com zero <c>BancaAvaliador</c> como DadosInconsistentes (500). O endpoint desta
    /// issue não depende de avaliador nenhum — só de <c>AtaCaminho</c>.
    /// </param>
    private static async Task<int> SemearBancaAsync(
        WebRootIsolatedApiFactory factory,
        string? ataCaminho = null,
        StatusTcc status = StatusTcc.AguardandoDefesa,
        decimal? notaFinal = null,
        bool comAvaliador = false)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdProfessor, Nome = "Professor Teste", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdProfessor,
            Status = status,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca
        {
            TccId = tcc.Id,
            DataHora = DateTime.UtcNow.AddDays(-1),
            Local = "Sala de Teste",
            NotaFinal = notaFinal
        };

        if (ataCaminho != null)
            banca.AtaCaminho = ataCaminho;

        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        if (comAvaliador)
        {
            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = IdProfessor });
            await context.SaveChangesAsync();
        }

        return banca.Id;
    }

    /// <summary>
    /// Grava um arquivo em <c>{webroot}/uploads/atas</c> reproduzindo o layout que o
    /// LocalStorageService cria em produção, e devolve o caminho relativo persistido.
    /// </summary>
    private static string GravarAtaEmDisco(WebRootIsolatedApiFactory factory, byte[] conteudo)
    {
        var nomeFisico = $"{Guid.NewGuid()}_ata-assinada.pdf";
        Directory.CreateDirectory(factory.PastaAtas);
        File.WriteAllBytes(Path.Combine(factory.PastaAtas, nomeFisico), conteudo);
        return $"/uploads/atas/{nomeFisico}";
    }

    private static MultipartFormDataContent MontarFormResultado(byte[] conteudoAta, string nomeArquivo = "ata-assinada.pdf")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("85.0"), "notaFinal" }
        };

        var arquivo = new ByteArrayContent(conteudoAta);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(arquivo, "arquivoAta", nomeArquivo);

        return form;
    }

    // ───────────────────────── Caminho feliz ─────────────────────────

    [Fact]
    public async Task ArquivoAnexadoEmRegistrarResultado_PodeSerBaixadoIntegro()
    {
        // A asserção que prova a issue: o arquivo servido é BYTE A BYTE o mesmo que o
        // Coordenador anexou — não o PDF gerado pelo QuestPDF, não um arquivo truncado.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var conteudoEnviado = ConteudoArquivoTeste.PdfComCorpo("copia-assinada-pela-banca");

        var upload = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(conteudoEnviado));
        upload.EnsureSuccessStatusCode();

        var download = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(conteudoEnviado, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Sucesso_RespondeComoAnexoENomeDerivadoDoIdDaBanca()
    {
        // Content-Disposition de attachment com nome derivado do id ("ata-assinada-{id}.pdf"):
        // o nome original enviado pelo Coordenador não vaza na resposta (mesmo critério de
        // DownloadEntrega). O media type é octet-stream de propósito (D3): arquivos legados
        // anteriores a esta issue nunca foram validados e podem não ser PDF.
        using var factory = new WebRootIsolatedApiFactory();
        var caminho = GravarAtaEmDisco(factory, ConteudoArquivoTeste.PdfComCorpo("legado"));
        var bancaId = await SemearBancaAsync(factory, ataCaminho: caminho);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Contains($"ata-assinada-{bancaId}.pdf", disposition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ata-assinada.pdf\"", disposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BancaLegada_ComAtaCaminhoJaGravado_EhServidaSemMigracao()
    {
        // RF-04: bancas cujo AtaCaminho foi preenchido antes desta issue (desde a migration
        // AddNotaEAtaNaBanca) ficam acessíveis pelo endpoint novo sem backfill nenhum.
        using var factory = new WebRootIsolatedApiFactory();
        var conteudoLegado = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }; // nem PDF é
        var caminho = GravarAtaEmDisco(factory, conteudoLegado);
        var bancaId = await SemearBancaAsync(factory, ataCaminho: caminho, status: StatusTcc.Finalizado, notaFinal: 90m);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(conteudoLegado, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AtaAssinada_ETambemAtaGerada_SaoDocumentosDistintos()
    {
        // Anti-regressão do equívoco que originou a issue: os dois endpoints existem lado a
        // lado e devolvem conteúdos diferentes para a MESMA banca.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory, comAvaliador: true);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var conteudoEnviado = ConteudoArquivoTeste.PdfComCorpo("assinada-a-mao");
        var upload = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(conteudoEnviado));
        upload.EnsureSuccessStatusCode();

        var assinada = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");
        var gerada = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        Assert.Equal(HttpStatusCode.OK, assinada.StatusCode);
        Assert.Equal(HttpStatusCode.OK, gerada.StatusCode);

        var bytesAssinada = await assinada.Content.ReadAsByteArrayAsync();
        var bytesGerada = await gerada.Content.ReadAsByteArrayAsync();

        Assert.Equal(conteudoEnviado, bytesAssinada);
        Assert.NotEqual(bytesAssinada, bytesGerada);
        Assert.Equal("application/pdf", gerada.Content.Headers.ContentType?.MediaType);
    }

    // ───────────────────────── Os três 404 ─────────────────────────

    [Fact]
    public async Task BancaInexistente_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/banca/999999/ata-assinada");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Banca não encontrada.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BancaSemCopiaAnexada_Retorna404ComMensagemPropria()
    {
        // AtaCaminho é NOT NULL com default string.Empty: "não tem arquivo" se manifesta como
        // string vazia, nunca como null. A mensagem é distinta da de "banca não encontrada"
        // para o Coordenador saber que a banca existe, só não tem cópia anexada.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("Nenhuma cópia assinada", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("Banca não encontrada", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaminhoPersistidoMasArquivoAusenteEmDisco_Retorna404ENao500()
    {
        // AbrirLeituraAsync devolve null quando o arquivo sumiu do storage — o endpoint
        // responde 404 em vez de estourar uma exceção não tratada.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory, ataCaminho: "/uploads/atas/nunca-existiu.pdf");
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Arquivo não encontrado.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArquivoApagadoDoStorageDepoisDoRegistro_Retorna404()
    {
        // Variante realista do caso acima: o registro passou pelo fluxo real e o arquivo foi
        // removido do disco depois (limpeza de storage, restore parcial de backup).
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var upload = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(ConteudoArquivoTeste.PdfComCorpo("sera-apagado")));
        upload.EnsureSuccessStatusCode();

        foreach (var arquivo in Directory.GetFiles(factory.PastaAtas))
            File.Delete(arquivo);

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Arquivo não encontrado.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../secrets.txt")]
    [InlineData("/uploads/../../appsettings.json")]
    public async Task CaminhoPersistidoForaDeUploads_Retorna404ENaoVazaExcecao(string caminhoMalicioso)
    {
        // ResolverCaminhoConfinado lança InvalidOperationException para qualquer caminho que
        // escape de {webroot}/uploads. O endpoint captura e responde 404 — sem 500, sem
        // stack trace, sem revelar o caminho.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory, ataCaminho: caminhoMalicioso);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("Arquivo não encontrado.", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appsettings", corpo, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────── Guarda de papel ─────────────────────────

    [Theory]
    [InlineData("Aluno")]
    [InlineData("Professor")]
    public async Task PapelDiferenteDeCoordenador_Retorna403(string papel)
    {
        // [Authorize(Roles = "Coordenador")] está na classe do controller: nem o aluno dono do
        // TCC nem o orientador têm acesso ao arquivo bruto (RNF explícito da issue — a issue
        // não amplia o acesso a nenhum outro papel).
        using var factory = new WebRootIsolatedApiFactory();
        var caminho = GravarAtaEmDisco(factory, ConteudoArquivoTeste.AssinaturaPdf);
        var bancaId = await SemearBancaAsync(factory, ataCaminho: caminho);
        var client = factory.CreateClientAutenticado(papel == "Aluno" ? IdAluno : IdProfessor, papel);

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SemAutenticacao_Retorna401()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var caminho = GravarAtaEmDisco(factory, ConteudoArquivoTeste.AssinaturaPdf);
        var bancaId = await SemearBancaAsync(factory, ataCaminho: caminho);
        var client = factory.CreateClient(); // sem os cabeçalhos do TestAuthHandler

        var response = await client.GetAsync($"/api/coordenador/banca/{bancaId}/ata-assinada");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PapelNaoAutorizado_NaoTocaOStorage_MesmoComBancaInexistente()
    {
        // A guarda de papel roda antes de qualquer consulta: um Aluno recebe 403 (e não 404)
        // mesmo para um id de banca que não existe.
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var response = await client.GetAsync("/api/coordenador/banca/999999/ata-assinada");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ───────────────────────── Isolamento entre bancas ─────────────────────────

    [Fact]
    public async Task CadaBancaServeApenasOProprioArquivo()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var conteudoA = ConteudoArquivoTeste.PdfComCorpo("ata-da-banca-A");
        var conteudoB = ConteudoArquivoTeste.PdfComCorpo("ata-da-banca-B");

        int bancaA, bancaB;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });

            var tccA = new Tcc { Titulo = "TCC A", Resumo = "R", AlunoId = IdAluno, Status = StatusTcc.Finalizado, DataCriacao = DateTime.UtcNow };
            var tccB = new Tcc { Titulo = "TCC B", Resumo = "R", AlunoId = IdAluno, Status = StatusTcc.Finalizado, DataCriacao = DateTime.UtcNow };
            context.Tccs.AddRange(tccA, tccB);
            await context.SaveChangesAsync();

            var b1 = new Banca { TccId = tccA.Id, DataHora = DateTime.UtcNow, Local = "S1", NotaFinal = 80m, AtaCaminho = GravarAtaEmDisco(factory, conteudoA) };
            var b2 = new Banca { TccId = tccB.Id, DataHora = DateTime.UtcNow, Local = "S2", NotaFinal = 80m, AtaCaminho = GravarAtaEmDisco(factory, conteudoB) };
            context.Banca.AddRange(b1, b2);
            await context.SaveChangesAsync();

            bancaA = b1.Id;
            bancaB = b2.Id;
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var respostaA = await client.GetAsync($"/api/coordenador/banca/{bancaA}/ata-assinada");
        var respostaB = await client.GetAsync($"/api/coordenador/banca/{bancaB}/ata-assinada");

        Assert.Equal(conteudoA, await respostaA.Content.ReadAsByteArrayAsync());
        Assert.Equal(conteudoB, await respostaB.Content.ReadAsByteArrayAsync());
    }

    // ───────────────────────── Persistência do caminho ─────────────────────────

    [Fact]
    public async Task RegistrarResultado_PersisteAtaCaminhoApontandoParaOArquivoGravado()
    {
        // Elo entre as duas metades da issue: o que UploadAsync devolve é exatamente o que
        // fica em Banca.AtaCaminho e o que GetAtaAssinada usa para reabrir o arquivo.
        using var factory = new WebRootIsolatedApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var upload = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(ConteudoArquivoTeste.AssinaturaPdf));
        upload.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var banca = await context.Banca.FirstAsync(b => b.Id == bancaId);

        Assert.StartsWith("/uploads/atas/", banca.AtaCaminho, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(factory.PastaAtas));
    }
}
