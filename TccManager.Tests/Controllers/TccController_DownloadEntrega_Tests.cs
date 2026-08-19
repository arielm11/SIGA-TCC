using System.Net;
using System.Text;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #69, item 1 — GET /api/tcc/entregas/{idEntrega}/download.
///
/// O arquivo da entrega deixou de ser servido por UseStaticFiles (URL adivinhável, sem
/// nenhuma autorização) e passou a ter um endpoint autenticado com autorização explícita
/// no corpo do método: Aluno dono do TCC, Orientador do TCC, qualquer Coordenador ou
/// Professor avaliador da banca daquele TCC. Qualquer outro autenticado recebe 404 — a
/// mesma resposta de "entrega inexistente", de propósito: não autorizado e inexistente são
/// indistinguíveis para quem pergunta, fechando o oráculo de enumeração de ids de Entrega
/// (achado A01-2, docs/seguranca/2026-08-18-fix-upload-storage-hardening.md).
///
/// Os arquivos físicos são gravados no WebRootPath temporário da
/// <see cref="WebRootIsolatedApiFactory"/> — nunca no wwwroot real do projeto.
/// </summary>
public class TccController_DownloadEntrega_Tests
{
    private const int IdAlunoDono = 10;
    private const int IdAlunoDeOutroTcc = 11;
    private const int IdOrientador = 20;
    private const int IdProfessorAvaliador = 21;
    private const int IdProfessorSemVinculo = 22;
    private const int IdCoordenador = 30;

    private static readonly byte[] ConteudoDoArquivo =
        Encoding.ASCII.GetBytes("%PDF-1.7\nconteudo-de-teste-da-entrega\n%%EOF");

    private sealed record Semeadura(int TccId, int EntregaId, int EntregaDeOutroTccId);

    /// <param name="gravarArquivoFisico">
    /// Quando false, a Entrega é criada no banco apontando para um caminho que não existe em
    /// disco — cenário de arquivo removido/perdido, que deve resultar em 404.
    /// </param>
    private static async Task<Semeadura> SemearAsync(
        WebRootIsolatedApiFactory factory,
        bool gravarArquivoFisico = true)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAlunoDono, Nome = "Aluno Dono", Email = "dono@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdAlunoDeOutroTcc, Nome = "Outro Aluno", Email = "outro-aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = IdProfessorAvaliador, Nome = "Avaliador", Email = "avaliador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = IdProfessorSemVinculo, Nome = "Prof Sem Vinculo", Email = "sem-vinculo@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
            new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC do Aluno Dono",
            Resumo = "Resumo",
            AlunoId = IdAlunoDono,
            OrientadorId = IdOrientador,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };

        var outroTcc = new Tcc
        {
            Titulo = "TCC de Terceiro",
            Resumo = "Resumo",
            AlunoId = IdAlunoDeOutroTcc,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };

        context.Tccs.AddRange(tcc, outroTcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(5), Local = "Sala 1" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, ProfessorId = IdProfessorAvaliador });
        await context.SaveChangesAsync();

        var entrega = new Entrega
        {
            TccId = tcc.Id,
            Titulo = "Versão Parcial",
            ArquivoCaminho = GravarArquivo(factory, "entrega-do-dono.pdf", gravarArquivoFisico),
            Tipo = TipoEntrega.Parcial,
            DataEnvio = DateTime.UtcNow
        };

        var entregaDeOutroTcc = new Entrega
        {
            TccId = outroTcc.Id,
            Titulo = "Entrega de Terceiro",
            ArquivoCaminho = GravarArquivo(factory, "entrega-de-terceiro.pdf", gravarArquivoFisico),
            Tipo = TipoEntrega.Parcial,
            DataEnvio = DateTime.UtcNow
        };

        context.Entregas.AddRange(entrega, entregaDeOutroTcc);
        await context.SaveChangesAsync();

        return new Semeadura(tcc.Id, entrega.Id, entregaDeOutroTcc.Id);
    }

    /// <summary>
    /// Grava o arquivo físico no web root isolado, reproduzindo o layout que o
    /// LocalStorageService cria em produção (/uploads/entregas/{guid}_{nome}), e devolve o
    /// caminho relativo que fica persistido em Entrega.ArquivoCaminho.
    /// </summary>
    private static string GravarArquivo(WebRootIsolatedApiFactory factory, string nomeLogico, bool gravar)
    {
        var nomeFisico = $"{Guid.NewGuid()}_{nomeLogico}";

        if (gravar)
        {
            Directory.CreateDirectory(factory.PastaEntregas);
            File.WriteAllBytes(Path.Combine(factory.PastaEntregas, nomeFisico), ConteudoDoArquivo);
        }

        return $"/uploads/entregas/{nomeFisico}";
    }

    private static async Task AssertConteudoDoArquivoAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ConteudoDoArquivo, await response.Content.ReadAsByteArrayAsync());
    }

    // ───────────────────────── Quem PODE baixar ─────────────────────────

    [Fact]
    public async Task AlunoDonoDoTcc_Retorna200ComConteudoDoArquivo()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoDono, "Aluno");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        await AssertConteudoDoArquivoAsync(response);
    }

    [Fact]
    public async Task AlunoDonoDoTcc_RecebeContentDispositionSemNomeOriginal()
    {
        // O nome exposto é derivado do id da entrega ("entrega-{id}.pdf"): o nome original
        // enviado pelo aluno (potencial PII/vazamento de contexto) não aparece na resposta.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoDono, "Aluno");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Contains($"entrega-{s.EntregaId}.pdf", disposition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("entrega-do-dono.pdf", disposition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrientadorDoTcc_Retorna200()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        await AssertConteudoDoArquivoAsync(response);
    }

    [Fact]
    public async Task Coordenador_Retorna200MesmoSemVinculoComOTcc()
    {
        // O papel Coordenador autoriza globalmente (User.IsInRole), sem exigir vínculo.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        await AssertConteudoDoArquivoAsync(response);
    }

    [Fact]
    public async Task ProfessorAvaliadorDaBancaDoTcc_Retorna200()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessorAvaliador, "Professor");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        await AssertConteudoDoArquivoAsync(response);
    }

    // ───────────────────────── Quem NÃO pode baixar ─────────────────────────

    [Fact]
    public async Task AlunoDeOutroTcc_Retorna404()
    {
        // Núcleo do achado: antes, qualquer um com a URL baixava o arquivo. A resposta é 404
        // (não 403) para não revelar que a entrega existe a quem não tem acesso a ela.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoDeOutroTcc, "Aluno");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AlunoDono_TentandoBaixarEntregaDeOutroTcc_Retorna404()
    {
        // Simetria do caso acima: ser dono de UM TCC não dá acesso à entrega de outro.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoDono, "Aluno");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaDeOutroTccId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProfessorSemVinculoComOTcc_Retorna404()
    {
        // Nem orientador, nem avaliador da banca: ter o papel "Professor" não basta.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessorSemVinculo, "Professor");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProfessorAvaliador_TentandoBaixarEntregaDeTccDeOutraBanca_Retorna404()
    {
        // O vínculo é verificado contra o TCC da entrega pedida (ba.Banca.TccId == tcc.Id),
        // não contra "ser avaliador de alguma banca".
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessorAvaliador, "Professor");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaDeOutroTccId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SemAutenticacao_Retorna401()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClient(); // sem os cabeçalhos do TestAuthHandler

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ───────────────────────── Recurso ausente ─────────────────────────

    [Fact]
    public async Task EntregaInexistente_Retorna404()
    {
        using var factory = new WebRootIsolatedApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/tcc/entregas/999999/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Entrega não encontrada.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArquivoFisicoAusenteEmDisco_Retorna404ENaoQuebra()
    {
        // Registro no banco existe, arquivo não: IStorageService.AbrirLeituraAsync devolve
        // null e o endpoint responde 404 em vez de estourar FileNotFoundException (500).
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory, gravarArquivoFisico: false);
        var client = factory.CreateClientAutenticado(IdAlunoDono, "Aluno");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Arquivo não encontrado.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArquivoFisicoAusente_NaoAutorizado_Retorna404ComMensagemDeEntregaNaoAutorizacao()
    {
        // Ordem das checagens: autorização antes de tocar no storage — um usuário não
        // autorizado recebe exatamente a mesma resposta ("Entrega não encontrada.") que
        // receberia se a entrega não existisse, sem nunca chegar a consultar o storage.
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory, gravarArquivoFisico: false);
        var client = factory.CreateClientAutenticado(IdProfessorSemVinculo, "Professor");

        var response = await client.GetAsync($"/api/tcc/entregas/{s.EntregaId}/download");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Entrega não encontrada.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ───────────────────────── Fluxo ponta a ponta ─────────────────────────

    [Fact]
    public async Task ArquivoEnviadoPeloProprioEndpointDeUpload_PodeSerBaixadoIntegro()
    {
        // Fecha o ciclo real: o que UploadAsync grava é exatamente o que
        // AbrirLeituraAsync devolve (mesmo caminho relativo, mesmo confinamento).
        using var factory = new WebRootIsolatedApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoDono, "Aluno");

        var conteudoEnviado = Encoding.ASCII.GetBytes("%PDF-1.4\nupload-e-download-ponta-a-ponta");

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Entrega Parcial"), "tituloEntrega" },
            { new StringContent(TipoEntrega.Parcial.ToString()), "tipo" },
            { new ByteArrayContent(conteudoEnviado), "arquivo", "trabalho.pdf" }
        };

        var upload = await client.PostAsync("/api/tcc/entregas", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        int entregaId;
        using (var context = factory.CriarContextoDireto())
        {
            var entrega = context.Entregas.Single(e => e.Titulo == "Entrega Parcial");
            entregaId = entrega.Id;
        }

        var download = await client.GetAsync($"/api/tcc/entregas/{entregaId}/download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(conteudoEnviado, await download.Content.ReadAsByteArrayAsync());
    }

    // ───────────────────────── Guarda de regressão do A01-1 ─────────────────────────

    [Fact]
    public async Task CaminhoEstaticoDeUploads_NaoEhMaisServido()
    {
        // Núcleo do achado A01-1 (docs/seguranca/2026-08-18-fix-upload-storage-hardening.md):
        // app.UseStaticFiles() foi removido de Program.cs para que /uploads/** deixe de ser
        // acessível sem autenticação. Sem este teste, reintroduzir UseStaticFiles() no futuro
        // (ex.: para servir um favicon) reabriria o acesso público a wwwroot/uploads inteiro,
        // sem que nenhum dos outros 474 testes acusasse a regressão.
        using var factory = new WebRootIsolatedApiFactory();
        await SemearAsync(factory);

        // Cliente sem autenticação: se o caminho estático ainda existisse, bastaria a URL.
        var client = factory.CreateClient();

        var resposta = await client.GetAsync("/uploads/entregas/entrega-do-dono.pdf");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }
}
