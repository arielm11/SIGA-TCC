using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

public class CoordenadorController_RegistrarResultadoBanca_Tests
{
    private const int idCoordenador = 1;
    private const int idAluno = 10;
    private const int idProfessor = 20;

    private async Task<(TccApiFactory factory, int bancaId, int tccId)> PrepararCenarioComBancaPendente()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Id = idAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var professor = new Usuario { Id = idProfessor, Nome = "Professor Teste", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };

        context.Usuarios.Add(aluno);
        context.Usuarios.Add(professor);

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo de teste",
            AlunoId = idAluno,
            OrientadorId = idProfessor,
            Status = StatusTcc.AguardandoDefesa,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        // Entrega Final — necessária para o cenário de "reprovado em banca" ser
        // distinguido de "rejeitado na proposta" pela tela do aluno (Bug #1, item 4)
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

    private MultipartFormDataContent MontarFormResultado(decimal nota, string? motivo = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(nota.ToString(System.Globalization.CultureInfo.InvariantCulture)), "notaFinal");

        if (motivo != null)
            form.Add(new StringContent(motivo), "motivoReprovacao");

        // PDF fake mínimo só para passar da validação de "arquivo obrigatório"
        var pdfFake = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
        pdfFake.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdfFake, "arquivoAta", "ata-teste.pdf");

        return form;
    }

    [Fact]
    public async Task Caso1_NotaAlta_DeveFinalizarTcc()
    {
        // Arrange
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(85.0m)); // escala 0-100: 85 aprova com folga

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Finalizado, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task Caso2_NotaBaixa_DeveReprovarTccComMotivo()
    {
        // Arrange
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        const string motivoEsperado = "Trabalho não atendeu aos requisitos metodológicos mínimos.";

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(40.0m, motivoEsperado)); // escala 0-100: 40 reprova

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.Equal(motivoEsperado, tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task Caso2b_NotaBaixa_SemMotivo_DeveRetornarBadRequest()
    {
        // Arrange — valida a regra de negócio no backend, independente do
        // botão desabilitado no frontend (defesa em profundidade)
        var (factory, bancaId, _) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(40.0m, motivo: null)); // escala 0-100: 40 reprova

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Caso3_NotaExatamenteNoLimite_DeveAprovar()
    {
        // Arrange — caso de fronteira: valida o operador ">=" usado na correção
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(60.0m)); // escala 0-100: exatamente o limite mínimo

        // Assert
        response.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Finalizado, tcc.Status);
    }

    [Fact]
    public async Task Caso4_ReprovadoEmBanca_DeveTerEntregaFinalAssociada()
    {
        // Arrange — valida a premissa usada pelo Client (FoiReprovadoNaBanca)
        // para distinguir reprovação em banca de rejeição de proposta:
        // a presença de uma Entrega do tipo Final.
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act
        await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(30.0m, "Não atingiu a nota mínima.")); // escala 0-100: 30 reprova

        // Assert — simula a query do GetMeuTcc (com Include) para garantir
        // que o sinal usado pelo front-end realmente existe nos dados
        using var context = factory.CriarContextoDireto();
        var tccComEntregas = await context.Tccs
            .Include(t => t.Entregas)
            .FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Reprovado, tccComEntregas.Status);
        Assert.Contains(tccComEntregas.Entregas, e => e.Tipo == TipoEntrega.Final);
    }

    [Fact]
    public async Task Caso5_BancaSemArquivoAta_DeveRetornarBadRequest()
    {
        // Arrange — regressão: garante que a validação de arquivo obrigatório
        // (que já existia antes da correção) continua funcionando
        var (factory, bancaId, _) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("80.0"), "notaFinal"); // escala 0-100, valor irrelevante para este teste
        // Propositalmente sem o campo "arquivoAta"

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado", form);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BancaInexistente_DeveRetornarNotFound()
    {
        // Arrange
        var factory = new TccApiFactory();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act
        var response = await client.PostAsync(
            "/api/coordenador/banca/9999/registrar-resultado",
            MontarFormResultado(80.0m)); // escala 0-100, valor irrelevante para este teste

        if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
        {
            var corpoErro = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Esperado NotFound, mas a API retornou 500. Corpo da resposta:\n{corpoErro}");
        }

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bug5_DuploLancamento_ApósAprovacao_DeveRetornarBadRequest()
    {

        // Arrange
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        // Act — primeiro lançamento: aprova o TCC normalmente
        var primeiraResposta = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(85.0m));
        primeiraResposta.EnsureSuccessStatusCode();

        // Act — segundo lançamento na MESMA banca, com nota diferente
        var segundaResposta = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(30.0m, "Tentativa de sobrescrever o resultado"));

        // Assert — o segundo lançamento deve ser bloqueado
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, segundaResposta.StatusCode);

        var corpoErro = await segundaResposta.Content.ReadAsStringAsync();
        Assert.Contains("já foi registrado", corpoErro, StringComparison.OrdinalIgnoreCase);

        // Assert — o estado original (da primeira chamada) deve permanecer intacto
        using var context = factory.CriarContextoDireto();
        var banca = await context.Banca.FirstAsync(b => b.Id == bancaId);
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(85.0m, banca.NotaFinal);
        Assert.Equal(StatusTcc.Finalizado, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task Bug5_DuploLancamento_ApósReprovacao_DeveRetornarBadRequest()
    {
        // Arrange
        var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        const string motivoOriginal = "Não atingiu os critérios mínimos de avaliação.";

        // Act — primeiro lançamento: reprova o TCC
        var primeiraResposta = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(40.0m, motivoOriginal));
        primeiraResposta.EnsureSuccessStatusCode();

        // Act — segundo lançamento na MESMA banca, tentando aprovar agora
        var segundaResposta = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado",
            MontarFormResultado(95.0m));

        // Assert — o segundo lançamento deve ser bloqueado, mesmo tentando "corrigir para aprovar"
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, segundaResposta.StatusCode);

        // Assert — o motivo e status originais (da reprovação) permanecem intactos
        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.FirstAsync(t => t.Id == tccId);

        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.Equal(motivoOriginal, tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task Bug2_MensagemDeErro_ArquivoAusente_DeveSerEspecificaENaoMencionarDuploLancamento()
    {
        // Arrange — banca ainda em AguardandoDefesa, então não é caso do Bug #5
        var (factory, bancaId, _) = await PrepararCenarioComBancaPendente();
        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var form = new MultipartFormDataContent();
        form.Add(new StringContent("80.0"), "notaFinal");
        // Propositalmente sem o campo "arquivoAta"

        // Act
        var response = await client.PostAsync(
            $"/api/coordenador/banca/{bancaId}/registrar-resultado", form);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var corpoErro = await response.Content.ReadAsStringAsync();
        Assert.Contains("arquivo", corpoErro, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("já foi registrado", corpoErro, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("já registrado", corpoErro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Regressao_NotaDecimal_NaoDeveSerAfetadaPelaCulturaDoSistema()
    {
        var culturaOriginal = Thread.CurrentThread.CurrentCulture;
        var culturaOriginalUI = Thread.CurrentThread.CurrentUICulture;

        try
        {
            // Arrange — simula um servidor configurado com cultura pt-BR
            var culturaPtBr = new System.Globalization.CultureInfo("pt-BR");
            Thread.CurrentThread.CurrentCulture = culturaPtBr;
            Thread.CurrentThread.CurrentUICulture = culturaPtBr;

            var (factory, bancaId, tccId) = await PrepararCenarioComBancaPendente();
            var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

            // Act — envia "85.5" no formato InvariantCulture (igual ao Client real)
            var response = await client.PostAsync(
                $"/api/coordenador/banca/{bancaId}/registrar-resultado",
                MontarFormResultado(85.5m));

            // Assert
            response.EnsureSuccessStatusCode();

            using var context = factory.CriarContextoDireto();
            var banca = await context.Banca.FirstAsync(b => b.Id == bancaId);

            // Se o bug de cultura reaparecer, este valor virá como 855 (ou
            // outra distorção), não 85.5 — é exatamente esse desvio que
            // queremos pegar antes que chegue em produção de novo.
            Assert.Equal(85.5m, banca.NotaFinal);
        }
        finally
        {
            // Restaura a cultura original da thread, mesmo se o teste falhar,
            // para não afetar outros testes que rodem depois deste no mesmo processo
            Thread.CurrentThread.CurrentCulture = culturaOriginal;
            Thread.CurrentThread.CurrentUICulture = culturaOriginalUI;
        }
    }
}