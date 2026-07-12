using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

public class TccController_EnviarEntrega_Tests
{
    private const int idAluno = 10;
    private const int idProfessor = 20;

    /// <summary>
    /// Variante da <see cref="TccApiFactory"/> que redireciona o WebRootPath do host de
    /// teste para um diretório temporário isolado por execução. O caminho de sucesso
    /// (RF4) grava fisicamente o arquivo em <c>WebRootPath/uploads/entregas</c>; sem esse
    /// redirecionamento os arquivos iriam para o wwwroot real do projeto TccManager.Api.
    /// O diretório é criado no construtor e removido no Dispose (mesmo em caso de falha),
    /// para não poluir o repositório nem acumular resíduo entre execuções.
    /// </summary>
    private sealed class EnviarEntregaApiFactory : TccApiFactory
    {
        public readonly string WebRootTemp;

        public EnviarEntregaApiFactory()
        {
            WebRootTemp = Path.Combine(
                Path.GetTempPath(),
                "siga-tcc-tests",
                "webroot",
                Guid.NewGuid().ToString());
            Directory.CreateDirectory(WebRootTemp);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(WebRootTemp);
        }

        public string PastaEntregas => Path.Combine(WebRootTemp, "uploads", "entregas");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                try
                {
                    if (Directory.Exists(WebRootTemp))
                        Directory.Delete(WebRootTemp, recursive: true);
                }
                catch
                {
                    // Limpeza best-effort: um arquivo eventualmente travado no SO não deve
                    // derrubar o teste nem mascarar o resultado real da asserção.
                }
            }
        }
    }

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

        // Bytes mínimos "%PDF" só para passar da validação de "arquivo obrigatório";
        // é a EXTENSÃO do nomeArquivo que determina o resultado do RF5.
        var arquivo = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(arquivo, "arquivo", nomeArquivo);

        return form;
    }

    // RF1 — bloqueio de entrega Final sem OrientadorId (variante RN03)
    [Fact]
    public async Task RF1_EntregaFinal_SemOrientador_DeveRetornarBadRequest()
    {
        using var factory = new EnviarEntregaApiFactory();
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

    // RF2 — bloqueio quando o TCC não está com Status = Aprovado
    [Theory]
    [InlineData(StatusTcc.Pendente)]
    [InlineData(StatusTcc.EmAndamento)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    public async Task RF2_TccNaoAprovado_DeveRetornarBadRequest(StatusTcc status)
    {
        using var factory = new EnviarEntregaApiFactory();
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
    [Fact]
    public async Task RF3_ReenvioComEntregaFinalExistente_DeveRetornarBadRequest()
    {
        using var factory = new EnviarEntregaApiFactory();
        var tccId = await SemearTccAsync(
            factory, StatusTcc.Aprovado, comOrientador: true, comEntregaFinal: true);
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
        using var factory = new EnviarEntregaApiFactory();
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
        using var factory = new EnviarEntregaApiFactory();
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

    // RF5 — bloqueio por extensão de arquivo não permitida
    [Theory]
    [InlineData("entrega.exe")]
    [InlineData("entrega.txt")]
    [InlineData("entrega.png")]
    [InlineData("entrega")]
    public async Task RF5_ExtensaoNaoPermitida_DeveRetornarBadRequest(string nomeArquivo)
    {
        using var factory = new EnviarEntregaApiFactory();
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
        using var factory = new EnviarEntregaApiFactory();
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
}
