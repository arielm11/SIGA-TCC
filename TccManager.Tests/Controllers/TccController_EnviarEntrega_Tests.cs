using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

public class TccController_EnviarEntrega_Tests
{
    private const int idAluno = 10;
    private const int idProfessor = 20;

    private static async Task<int> SemearTccAsync(
        TccApiFactory factory,
        StatusTcc status,
        bool comOrientador,
        bool comEntregaFinal = false)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = idAluno,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            SenhaHash = "x",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });

        if (comOrientador)
        {
            context.Usuarios.Add(new Usuario
            {
                Id = idProfessor,
                Nome = "Professor Teste",
                Email = "prof@teste.com",
                SenhaHash = "x",
                Tipo = TipoUsuario.Professor,
                Ativo = true
            });
        }

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo de teste",
            AlunoId = idAluno,
            OrientadorId = comOrientador ? idProfessor : null,
            Status = status,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        if (comEntregaFinal)
        {
            context.Entregas.Add(new Entrega
            {
                TccId = tcc.Id,
                Titulo = "Versão Final",
                ArquivoCaminho = "/uploads/entregas/fake.pdf",
                Tipo = TipoEntrega.Final,
                DataEnvio = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        return tcc.Id;
    }

    private static MultipartFormDataContent MontarFormEntrega(
        string tituloEntrega,
        TipoEntrega tipo,
        string nomeArquivo,
        string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(tituloEntrega), "tituloEntrega");
        form.Add(new StringContent(tipo.ToString()), "tipo");

        // Bytes mínimos de magic number correspondentes à EXTENSÃO do nomeArquivo, para
        // passar da validação de conteúdo (hardening da issue #69, item 2) além da
        // validação de "arquivo obrigatório"; é a EXTENSÃO do nomeArquivo que determina
        // o resultado do RF5 — o conteúdo aqui só precisa ser plausível para ela.
        var arquivo = new ByteArrayContent(ObterBytesMagicNumberPara(nomeArquivo));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(arquivo, "arquivo", nomeArquivo);

        return form;
    }

    private static byte[] ObterBytesMagicNumberPara(string nomeArquivo)
    {
        var extensao = Path.GetExtension(nomeArquivo).ToLowerInvariant();

        return extensao switch
        {
            ".doc" => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 },
            ".docx" or ".zip" => new byte[] { 0x50, 0x4B, 0x03, 0x04 },
            // Default (inclui ".pdf" e extensões não permitidas, cujo conteúdo é
            // irrelevante porque o bloqueio ocorre antes, pela extensão).
            _ => new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }
        };
    }

    // RF1 — bloqueio de entrega Final sem OrientadorId (variante RN03)
    [Fact]
    public async Task RF1_EntregaFinal_SemOrientador_DeveRetornarBadRequest()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Versão Final", TipoEntrega.Final, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("RN03", corpo, StringComparison.OrdinalIgnoreCase);

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.False(houveEntrega);
    }

    // RF2 — bloqueio quando o TCC não está em acompanhamento (nem Aprovado nem EmAndamento)
    //
    // Issue #82 (D4/Grupo B, seção 12 item 1): o caso [InlineData(StatusTcc.EmAndamento)] foi
    // REMOVIDO desta Theory, e essa remoção é a mudança de sinal esperada — EmAndamento passou a
    // ser um estado válido para upload (é justamente o estado em que o TCC fica depois da
    // primeira entrega; mantê-lo aqui travaria o aluno na segunda). O caso positivo correspondente
    // vive em TccController_TransicaoEmAndamento_Tests, junto com o resto do gatilho.
    [Theory]
    [InlineData(StatusTcc.Pendente)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    public async Task RF2_TccNaoAprovado_DeveRetornarBadRequest(StatusTcc status)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, status, comOrientador: true);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Entrega Parcial", TipoEntrega.Parcial, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("aprovado", corpo, StringComparison.OrdinalIgnoreCase);

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.False(houveEntrega);
    }

    // RF3 — bloqueio de reenvio quando já existe uma Entrega Tipo=Final
    //
    // Issue #82 (seção 12, item 6): o seed passou de Aprovado para EmAndamento. "Aprovado COM
    // entrega" virou um estado impossível depois desta issue (a invariante é
    // Aprovado <=> zero Entregas), e o backfill corrige exatamente essas linhas no legado. O
    // resultado do teste não muda — o bloqueio da Final acontece antes do gatilho —, mas o seed
    // deixa de ser ficção.
    [Fact]
    public async Task RF3_ReenvioComEntregaFinalExistente_DeveRetornarBadRequest()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(
            factory, StatusTcc.EmAndamento, comOrientador: true, comEntregaFinal: true);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Nova Parcial", TipoEntrega.Parcial, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("FINAL", corpo, StringComparison.OrdinalIgnoreCase);

        // Nenhuma nova entrega deve ser gravada além da Final pré-existente
        using var context = factory.CriarContextoDireto();
        var totalEntregas = await context.Entregas.CountAsync(e => e.TccId == tccId);
        Assert.Equal(1, totalEntregas);
    }

    // RF4 — caminho de sucesso: entrega Parcial (não exige orientador)
    [Fact]
    public async Task RF4_EntregaParcial_ComTccAprovado_DeveRetornarOk()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Entrega Parcial", TipoEntrega.Parcial, "entrega.pdf"));

        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var entrega = await context.Entregas.SingleAsync(e => e.TccId == tccId);
        Assert.Equal(TipoEntrega.Parcial, entrega.Tipo);
        Assert.Equal("Entrega Parcial", entrega.Titulo);
        Assert.StartsWith("/uploads/entregas/", entrega.ArquivoCaminho);

        // O arquivo físico deve ter sido gravado no web root temporário isolado,
        // e não no wwwroot real do projeto.
        Assert.True(Directory.Exists(factory.PastaEntregas));
        Assert.Single(Directory.GetFiles(factory.PastaEntregas));
    }

    // RF4 — caminho de sucesso: entrega Final com orientador definido
    [Fact]
    public async Task RF4_EntregaFinal_ComOrientador_DeveRetornarOk()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: true);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Versão Final", TipoEntrega.Final, "entrega.pdf"));

        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var entrega = await context.Entregas.SingleAsync(e => e.TccId == tccId);
        Assert.Equal(TipoEntrega.Final, entrega.Tipo);

        Assert.True(Directory.Exists(factory.PastaEntregas));
        Assert.Single(Directory.GetFiles(factory.PastaEntregas));
    }

    // Issue #73: tituloEntrega é parâmetro de form escalar, não passa pelo
    // FluentValidationActionFilter (que só intercepta DTOs de corpo) — o [StringLength] no
    // próprio parâmetro precisa gerar 400 via o model binding padrão do [ApiController].
    [Fact]
    public async Task TituloEntregaComMaisDe200Caracteres_DeveRetornarBadRequest()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega(new string('a', 201), TipoEntrega.Parcial, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.False(houveEntrega);
    }

    [Fact]
    public async Task TituloEntregaComExatamente200Caracteres_DeveRetornarOk()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega(new string('a', 200), TipoEntrega.Parcial, "entrega.pdf"));

        response.EnsureSuccessStatusCode();
    }

    // Achado A10-1 da revisão de segurança (docs/seguranca/2026-08-27-fix-campos-texto-livre-maxlength.md):
    // HtmlSanitizer codifica "&" em "&amp;" (5x maior) — um título de 200 "&" passa no limite
    // cru mas viraria 1000 caracteres sanitizados, estourando a coluna nvarchar(200). A
    // checagem no controller mede o valor JÁ sanitizado.
    [Fact]
    public async Task TituloEntregaDentroDoLimiteCru_MasQueExpandeAoSanitizar_DeveRetornarBadRequest()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega(new string('&', 200), TipoEntrega.Parcial, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.False(houveEntrega);

        // Núcleo do achado: nenhum arquivo órfão sobra em storage — a checagem acontece
        // ANTES do upload.
        Assert.True(!Directory.Exists(factory.PastaEntregas) || Directory.GetFiles(factory.PastaEntregas).Length == 0);
    }

    // RF5 — bloqueio por extensão de arquivo não permitida
    [Theory]
    [InlineData("entrega.exe")]
    [InlineData("entrega.txt")]
    [InlineData("entrega.png")]
    [InlineData("entrega")]
    public async Task RF5_ExtensaoNaoPermitida_DeveRetornarBadRequest(string nomeArquivo)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: true);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Entrega Parcial", TipoEntrega.Parcial, nomeArquivo));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("formato", corpo, StringComparison.OrdinalIgnoreCase);

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.False(houveEntrega);
    }

    // RF5 — extensões permitidas devem ser aceitas (complemento do caso de sucesso)
    [Theory]
    [InlineData("entrega.pdf", "application/pdf")]
    [InlineData("entrega.doc", "application/msword")]
    [InlineData("entrega.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("entrega.zip", "application/zip")]
    public async Task RF5_ExtensaoPermitida_DeveRetornarOk(string nomeArquivo, string contentType)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Entrega Parcial", TipoEntrega.Parcial, nomeArquivo, contentType));

        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var houveEntrega = await context.Entregas.AnyAsync(e => e.TccId == tccId);
        Assert.True(houveEntrega);
    }

    // Issue #75: [Authorize(Roles = "Aluno")] em EnviarEntrega nunca tinha teste dedicado —
    // toda a suíte só exercitava esse endpoint autenticada como Aluno. [Authorize(Roles=...)]
    // roda como filtro de autorização antes do corpo da ação (não depende de nenhum dado no
    // banco), então nem precisa semear TCC para provar o 403.
    [Theory]
    [InlineData("Professor")]
    [InlineData("Coordenador")]
    public async Task PapelDiferenteDeAluno_DeveRetornarForbidden(string papel)
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(idProfessor, papel);

        var response = await client.PostAsync(
            "/api/tcc/entregas",
            MontarFormEntrega("Entrega Parcial", TipoEntrega.Parcial, "entrega.pdf"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Entregas.AnyAsync());
    }

    // Issue #75: nenhum endpoint de upload tinha limite de tamanho. [RequestSizeLimit]
    // (no atributo do método) protege a conexão Kestrel real, mas esse corte não é
    // reproduzido pelo TestServer em memória usado aqui (confirmado empiricamente: sem a
    // checagem explícita sobre IFormFile.Length dentro do método, um corpo de
    // MaxArquivoUploadBytes + 1024 bytes passava batido pelo TestServer direto até a
    // validação de conteúdo do arquivo, sem nenhuma rejeição por tamanho) — é a checagem
    // explícita, não o atributo, que este teste prova.
    [Fact]
    public async Task ArquivoAcimaDoLimiteDeTamanho_DeveSerRejeitado()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("Entrega Grande Demais"), "tituloEntrega");
        form.Add(new StringContent(TipoEntrega.Parcial.ToString()), "tipo");

        var conteudoGigante = new byte[TccManager.Api.Configuration.UploadLimits.MaxArquivoUploadBytes + 1024];
        // Prefixo com o magic number do PDF: prova que a rejeição é especificamente por
        // tamanho, não por a validação de conteúdo ter barrado primeiro.
        conteudoGigante[0] = 0x25; conteudoGigante[1] = 0x50; conteudoGigante[2] = 0x44; conteudoGigante[3] = 0x46;
        var arquivo = new ByteArrayContent(conteudoGigante);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(arquivo, "arquivo", "entrega-gigante.pdf");

        var response = await client.PostAsync("/api/tcc/entregas", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("tamanho máximo", corpo, StringComparison.OrdinalIgnoreCase);

        using var context = factory.CriarContextoDireto();
        Assert.False(await context.Entregas.AnyAsync(e => e.TccId == tccId));
    }
}
