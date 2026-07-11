using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Testes de integração da sanitização anti-XSS: exercitam o pipeline HTTP real
/// (WebApplicationFactory) para confirmar que payloads com HTML são limpos antes
/// de persistir, tanto no caminho application/json (SubmeterProposta) quanto no
/// caminho multipart/form-data (RegistrarResultadoBanca).
/// </summary>
public class SanitizacaoXss_Integracao_Tests
{
    private const int ID_ALUNO = 10;
    private const int ID_PROFESSOR = 20;
    private const int ID_COORDENADOR = 1;

    private const string PayloadScript = "<script>alert(1)</script>";

    private async Task<TccApiFactory> PrepararCenarioComAluno()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = ID_ALUNO,
            Nome = "Aluno Teste",
            Email = "aluno@teste.com",
            SenhaHash = "x",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        });
        await context.SaveChangesAsync();

        return factory;
    }

    [Fact]
    public async Task SubmeterProposta_ComScriptNoTituloEResumo_PersisteSemTagScript()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = $"Meu TCC {PayloadScript}",
            Resumo = $"Resumo do trabalho {PayloadScript} com conteúdo relevante."
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);

        // Assert — resposta bem-sucedida
        response.EnsureSuccessStatusCode();

        // Assert — valor persistido no banco não contém a tag <script>
        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.AlunoId == ID_ALUNO);

        Assert.DoesNotContain("<script", tcc.Titulo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", tcc.Titulo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", tcc.Titulo);
        Assert.DoesNotContain(">", tcc.Titulo);

        Assert.DoesNotContain("<script", tcc.Resumo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", tcc.Resumo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", tcc.Resumo);
        Assert.DoesNotContain(">", tcc.Resumo);

        // Assert — o texto legítimo em torno do payload é preservado
        Assert.Contains("Meu TCC", tcc.Titulo);
        Assert.Contains("Resumo do trabalho", tcc.Resumo);
    }

    [Fact]
    public async Task SubmeterProposta_ComScript_RespostaRetornadaTambemSemTagScript()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = $"Título {PayloadScript}",
            Resumo = $"Resumo {PayloadScript}"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);
        response.EnsureSuccessStatusCode();

        // Assert — o corpo devolvido ao cliente (que o front reexibe) já vem limpo
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script", corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", corpo, StringComparison.OrdinalIgnoreCase);

        var tccRetornado = await response.Content.ReadFromJsonAsync<Tcc>();
        Assert.NotNull(tccRetornado);
        Assert.DoesNotContain("<", tccRetornado!.Titulo);
        Assert.DoesNotContain("<", tccRetornado.Resumo);
    }

    [Fact]
    public async Task SubmeterProposta_ComImgOnError_RemoveAtributoDeEvento()
    {
        // Arrange
        var factory = await PrepararCenarioComAluno();
        var client = factory.CreateClientAutenticado(ID_ALUNO, "Aluno");

        var dto = new PropostaTccDto
        {
            Titulo = "TCC <img src=x onerror=\"alert(1)\"> válido",
            Resumo = "Resumo <b>importante</b> do trabalho."
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/tcc/proposta", dto);
        response.EnsureSuccessStatusCode();

        // Assert
        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.AlunoId == ID_ALUNO);

        Assert.DoesNotContain("onerror", tcc.Titulo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", tcc.Titulo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", tcc.Resumo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("importante", tcc.Resumo);
    }

    // ── Caminho multipart/form-data ─────────────────────────────────────────

    private async Task<(TccApiFactory factory, int bancaId, int tccId)> PrepararCenarioComBancaPendente()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario { Id = ID_ALUNO, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
        context.Usuarios.Add(new Usuario { Id = ID_PROFESSOR, Nome = "Professor Teste", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo de teste",
            AlunoId = ID_ALUNO,
            OrientadorId = ID_PROFESSOR,
            Status = StatusTcc.AguardandoDefesa,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        context.Entregas.Add(new Entrega
        {
            TccId = tcc.Id,
            Titulo = "Versão Final",
            ArquivoCaminho = "/uploads/entregas/fake.pdf",
            Tipo = TipoEntrega.Final,
            DataEnvio = DateTime.UtcNow
        });

        var banca = new Banca
        {
            TccId = tcc.Id,
            DataHora = DateTime.UtcNow.AddDays(1),
            Local = "Sala de Teste"
        };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        return (factory, banca.Id, tcc.Id);
    }

    [Fact]
    public async Task RegistrarResultadoBanca_ComScriptNoMotivo_PersisteSemTagScript()
    {
        // Arrange
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(ID_COORDENADOR, "Coordenador");

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("40.0"), "notaFinal"); // escala 0-100: reprova, exige motivo
        form.Add(new StringContent($"Reprovado {PayloadScript} por metodologia."), "motivoReprovacao");

        var pdfFake = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
        pdfFake.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdfFake, "arquivoAta", "ata-teste.pdf");

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado", form);

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.NotNull(tcc.MotivoRejeicao);
        Assert.DoesNotContain("<script", tcc.MotivoRejeicao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", tcc.MotivoRejeicao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", tcc.MotivoRejeicao);
        Assert.DoesNotContain(">", tcc.MotivoRejeicao);
        Assert.Contains("Reprovado", tcc.MotivoRejeicao);
        Assert.Contains("metodologia", tcc.MotivoRejeicao);
    }
}
