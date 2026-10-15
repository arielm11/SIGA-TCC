using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TccManager.Api.Services.Email;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #81 — veredito do orientador por entrega:
/// <c>POST api/orientador/entregas/{id}/aprovar</c> e <c>POST api/orientador/entregas/{id}/rejeitar</c>.
///
/// O que estes testes travam, por decisão de arquitetura:
/// - D3: são dois endpoints dedicados, distintos de <c>RegistrarFeedback</c> — e nenhum deles
///   dispara a notificação de feedback (assuntos de e-mail diferentes).
/// - D4: rejeitar exige motivo (validado por <c>RejeicaoDtoValidator</c>, o mesmo já usado pela
///   rejeição de proposta do Coordenador) e o motivo é persistido SANITIZADO em
///   <c>Entrega.Feedback</c>, sobrescrevendo o parecer anterior.
/// - D7: aprovar/rejeitar a Final decide se <c>DarAceiteFinal</c> passa ou não.
/// - D8: uma Final <c>Rejeitada</c> é terminal (409 em qualquer novo veredito sobre AQUELA linha),
///   enquanto uma Parcial rejeitada continua reavaliável.
/// - D9: só se registra veredito com o TCC em <c>Aprovado</c>/<c>EmAndamento</c>.
/// - D10: trilha de auditoria com ids, nunca com o texto do motivo.
/// - Guarda de vínculo: 404 (não 403) para um Professor que não é o orientador daquele TCC —
///   mesmo filtro/semântica de <c>RegistrarFeedback</c>; 403 fica para quem não é Professor,
///   barrado pelo <c>[Authorize(Roles = "Professor")]</c> do controller.
///
/// O efeito "rejeitar a Final reabre o envio para o aluno" é exercitado ponta a ponta em
/// <see cref="OrientadorController_CicloVeredictoFinal_Tests"/>, que precisa de upload real.
/// </summary>
public class OrientadorController_VeredictoEntrega_Tests
{
    private const int idAluno = 10;
    private const int idOrientador = 20;
    private const int idOutroProfessor = 21;
    private const int idCoordenador = 30;
    private const int idTcc = 1;
    private const int idEntregaParcial = 7;
    private const int idEntregaFinal = 8;

    private const string RotaAprovarParcial = "/api/orientador/entregas/7/aprovar";
    private const string RotaRejeitarParcial = "/api/orientador/entregas/7/rejeitar";
    private const string RotaAprovarFinal = "/api/orientador/entregas/8/aprovar";
    private const string RotaRejeitarFinal = "/api/orientador/entregas/8/rejeitar";
    private const string RotaAceiteFinal = "/api/orientador/tcc/1/aceite-final";

    private sealed class FactoryComFilaFake : TccApiFactory
    {
        private readonly FakeEmailQueue _fila;

        public FactoryComFilaFake(FakeEmailQueue fila) => _fila = fila;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(_fila);
            });
        }
    }

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = true };

    /// <summary>
    /// Semeia um TCC com DUAS entregas: uma Parcial (id 7) e uma Final (id 8). Ter as duas em
    /// todos os cenários é o que permite afirmar "aprovar/rejeitar a Parcial não teve efeito de
    /// sistema" comparando a Final antes e depois.
    /// </summary>
    private static async Task SemearAsync(
        TccApiFactory factory,
        StatusTcc statusTcc = StatusTcc.EmAndamento,
        StatusEntrega statusParcial = StatusEntrega.Pendente,
        StatusEntrega statusFinal = StatusEntrega.Pendente,
        string? feedbackInicial = null)
    {
        using var ctx = factory.CriarContextoDireto();

        ctx.Usuarios.AddRange(
            NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor),
            NovoUsuario(idOutroProfessor, "Outro Professor", "outro@teste.com", TipoUsuario.Professor),
            NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));

        ctx.Tccs.Add(new Tcc
        {
            Id = idTcc,
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = idAluno,
            OrientadorId = idOrientador,
            Status = statusTcc,
            DataCriacao = DateTime.UtcNow
        });

        ctx.Entregas.AddRange(
            new Entrega
            {
                Id = idEntregaParcial,
                TccId = idTcc,
                Titulo = "Capítulos 1 e 2",
                ArquivoCaminho = "/parcial.pdf",
                Tipo = TipoEntrega.Parcial,
                Status = statusParcial,
                Feedback = feedbackInicial,
                DataEnvio = DateTime.UtcNow.AddDays(-10)
            },
            new Entrega
            {
                Id = idEntregaFinal,
                TccId = idTcc,
                Titulo = "Versão Final",
                ArquivoCaminho = "/final.pdf",
                Tipo = TipoEntrega.Final,
                Status = statusFinal,
                Feedback = feedbackInicial,
                DataEnvio = DateTime.UtcNow
            });

        await ctx.SaveChangesAsync();
    }

    private static async Task<FactoryComFilaFake> FactoryPadraoAsync(
        FakeEmailQueue fila,
        StatusTcc statusTcc = StatusTcc.EmAndamento,
        StatusEntrega statusParcial = StatusEntrega.Pendente,
        StatusEntrega statusFinal = StatusEntrega.Pendente,
        string? feedbackInicial = null)
    {
        var factory = new FactoryComFilaFake(fila);
        await SemearAsync(factory, statusTcc, statusParcial, statusFinal, feedbackInicial);
        return factory;
    }

    private static async Task<Entrega> LerEntregaAsync(TccApiFactory factory, int idEntrega)
    {
        using var ctx = factory.CriarContextoDireto();
        return await ctx.Entregas.AsNoTracking().FirstAsync(e => e.Id == idEntrega);
    }

    private static async Task<StatusTcc> LerStatusTccAsync(TccApiFactory factory)
    {
        using var ctx = factory.CriarContextoDireto();
        return (await ctx.Tccs.AsNoTracking().FirstAsync(t => t.Id == idTcc)).Status;
    }

    private static RejeicaoDto Motivo(string texto) => new() { Motivo = texto };

    // ── Aprovação ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AprovarEntregaParcial_RetornaOk_GravaVeredito_ESemNenhumEfeitoDeSistema()
    {
        // "Acontece nada" do documento de produto: aprovar/rejeitar uma Parcial só grava o
        // veredito daquela linha — não mexe no Tcc nem na Final.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarParcial, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Entrega aprovada.", await response.Content.ReadAsStringAsync());

        Assert.Equal(StatusEntrega.Aprovada, (await LerEntregaAsync(factory, idEntregaParcial)).Status);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusTccAsync(factory));
    }

    [Fact]
    public async Task AprovarEntrega_NaoSobrescreveOFeedbackJaRegistrado()
    {
        // Só a rejeição escreve em Feedback (D4). Aprovar preserva a avaliação textual/nota já
        // registrada por RegistrarFeedback.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, feedbackInicial: "Bom trabalho, ajuste as referências.");
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarParcial, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bom trabalho, ajuste as referências.", (await LerEntregaAsync(factory, idEntregaParcial)).Feedback);
    }

    [Fact]
    public async Task AprovarEntregaFinal_RetornaOk_ELiberaODarAceiteFinal()
    {
        // D7: é este o caminho que faz o aceite final voltar a ser possível.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var aprovacao = await client.PostAsync(RotaAprovarFinal, null);
        Assert.Equal(HttpStatusCode.OK, aprovacao.StatusCode);
        Assert.Equal(StatusEntrega.Aprovada, (await LerEntregaAsync(factory, idEntregaFinal)).Status);

        var aceite = await client.PostAsync(RotaAceiteFinal, null);

        Assert.Equal(HttpStatusCode.OK, aceite.StatusCode);
        Assert.Equal(StatusTcc.AguardandoDefesa, await LerStatusTccAsync(factory));
    }

    [Fact]
    public async Task AprovarEntrega_EnfileiraNotificacaoDeAprovacaoParaOAluno_ENaoADeFeedback()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarFinal, null);
        response.EnsureSuccessStatusCode();

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Entrega aprovada pelo orientador", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
        // Asserções sobre o corpo usam trechos ASCII de propósito: o renderizador aplica
        // WebUtility.HtmlEncode nos placeholders, que transforma acentuados em entidades
        // numéricas ("ã" -> "&#227;"). Comparar o texto acentuado cru daria falso negativo.
        Assert.Contains("TCC de Teste", msg.CorpoHtml, StringComparison.Ordinal);
        // D3/5.2: o veredito não passa pelo gatilho de RegistrarFeedback.
        Assert.DoesNotContain(fila.Mensagens, m => m.Assunto == "Feedback registrado na sua entrega");
    }

    // ── Rejeição ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RejeitarEntregaParcial_RetornaOk_GravaVeredictoEMotivo_ESemNenhumEfeitoDeSistema()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");
        const string motivo = "A revisão bibliográfica precisa de fontes primárias.";

        var response = await client.PostAsJsonAsync(RotaRejeitarParcial, Motivo(motivo));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Mensagem sem convite a reenviar: rejeitar Parcial não reabre ciclo nenhum.
        Assert.Equal("Entrega rejeitada.", await response.Content.ReadAsStringAsync());

        var parcial = await LerEntregaAsync(factory, idEntregaParcial);
        Assert.Equal(StatusEntrega.Rejeitada, parcial.Status);
        Assert.Equal(motivo, parcial.Feedback);

        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusTccAsync(factory));
    }

    [Fact]
    public async Task RejeitarEntregaFinal_RetornaOkComMensagemDeReabertura_EBloqueiaODarAceiteFinal()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Faltam os resultados do capítulo 4."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Entrega final rejeitada. O aluno já pode enviar uma nova versão.",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(StatusEntrega.Rejeitada, (await LerEntregaAsync(factory, idEntregaFinal)).Status);

        var aceite = await client.PostAsync(RotaAceiteFinal, null);

        Assert.Equal(HttpStatusCode.BadRequest, aceite.StatusCode);
        Assert.Contains(
            "A versão final foi rejeitada. Aguarde o novo envio do aluno.",
            await aceite.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusTccAsync(factory));
    }

    [Fact]
    public async Task RejeitarEntrega_SobrescreveOFeedbackAnterior()
    {
        // Trade-off explicitamente aceito por D4 (o motivo mora em Entrega.Feedback, sem coluna
        // nova). Se algum dia passar a haver coluna própria para o motivo, este teste é o ponto
        // de revisão.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, feedbackInicial: "Parecer anterior registrado via Avaliar.");
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarParcial, Motivo("Motivo definitivo da rejeição."));

        response.EnsureSuccessStatusCode();
        Assert.Equal("Motivo definitivo da rejeição.", (await LerEntregaAsync(factory, idEntregaParcial)).Feedback);
    }

    [Fact]
    public async Task RejeitarEntrega_ComScriptNoMotivo_PersisteSanitizadoSemTagScript()
    {
        // Mesma disciplina de CoordenadorController.RejeitarProposta (issue #73, achado A10-1):
        // o motivo é texto livre exibido ao aluno em MeuTcc.razor.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(
            RotaRejeitarFinal,
            Motivo("Rejeitada <script>alert('xss')</script> por falta de metodologia."));

        response.EnsureSuccessStatusCode();

        var final = await LerEntregaAsync(factory, idEntregaFinal);
        Assert.NotNull(final.Feedback);
        Assert.DoesNotContain("<script", final.Feedback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", final.Feedback);
        Assert.DoesNotContain(">", final.Feedback);
        Assert.Contains("Rejeitada", final.Feedback);
        Assert.Contains("metodologia", final.Feedback);
    }

    [Fact]
    public async Task RejeitarEntrega_MotivoVazio_RetornaBadRequest_ENaoAlteraNada()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        var final = await LerEntregaAsync(factory, idEntregaFinal);
        Assert.Equal(StatusEntrega.Pendente, final.Status);
        Assert.Null(final.Feedback);
    }

    [Fact]
    public async Task RejeitarEntrega_MotivoDentroDoLimiteCru_MasQueExpandeAoSanitizar_RetornaBadRequest()
    {
        // O RejeicaoDtoValidator mede o valor JÁ sanitizado (2000 "&" viram 10000 caracteres
        // como "&amp;") — o FluentValidationActionFilter corta antes do corpo da ação rodar,
        // então nada é gravado e nenhum e-mail sai.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo(new string('&', 2000)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
    }

    // ── D8: a Final rejeitada é terminal (e a Parcial rejeitada não é) ────────────────────

    [Fact]
    public async Task RejeitarEntregaFinal_DuasVezes_SegundaRetornaConflict_SemQuebrarEmErro500()
    {
        // A segunda tentativa precisa falhar de forma CONTROLADA: sem D8 isso viraria um UPDATE
        // reintroduzindo a linha no índice único filtrado — 500 em vez de 409.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");
        const string motivoOriginal = "Faltam os resultados do capítulo 4.";

        var primeira = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo(motivoOriginal));
        primeira.EnsureSuccessStatusCode();

        var segunda = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Outro motivo qualquer."));

        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Contains(
            "Esta entrega final já foi rejeitada",
            await segunda.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // O veredito e o motivo da primeira rejeição continuam intactos, e o aluno não recebe
        // um segundo e-mail.
        var final = await LerEntregaAsync(factory, idEntregaFinal);
        Assert.Equal(StatusEntrega.Rejeitada, final.Status);
        Assert.Equal(motivoOriginal, final.Feedback);
        Assert.Single(fila.Mensagens);
    }

    [Fact]
    public async Task AprovarEntregaFinalJaRejeitada_RetornaConflict_SemQuebrarEmErro500()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, statusFinal: StatusEntrega.Rejeitada);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarFinal, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(StatusEntrega.Rejeitada, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task VeredictoDeEntregaParcialJaRejeitada_ContinuaAlteravel()
    {
        // D8 vale só para a Final (é ela que participa do índice único filtrado). Uma Parcial
        // rejeitada pode ser reavaliada — assimetria deliberada, documentada aqui para não ser
        // "corrigida" por engano.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, statusParcial: StatusEntrega.Rejeitada);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var novaRejeicao = await client.PostAsJsonAsync(RotaRejeitarParcial, Motivo("Ainda insuficiente."));
        Assert.Equal(HttpStatusCode.OK, novaRejeicao.StatusCode);

        var aprovacao = await client.PostAsync(RotaAprovarParcial, null);

        Assert.Equal(HttpStatusCode.OK, aprovacao.StatusCode);
        Assert.Equal(StatusEntrega.Aprovada, (await LerEntregaAsync(factory, idEntregaParcial)).Status);
    }

    // ── D9: guarda de estado do TCC ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    [InlineData(StatusTcc.Reprovado)]
    [InlineData(StatusTcc.Pendente)]
    public async Task AprovarEntrega_ComTccForaDeAcompanhamento_RetornaBadRequest(StatusTcc statusTcc)
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, statusTcc: statusTcc);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarFinal, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "enquanto o TCC está em acompanhamento",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
        Assert.Empty(fila.Mensagens);
    }

    [Theory]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    [InlineData(StatusTcc.Reprovado)]
    [InlineData(StatusTcc.Pendente)]
    public async Task RejeitarEntrega_ComTccForaDeAcompanhamento_RetornaBadRequest(StatusTcc statusTcc)
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, statusTcc: statusTcc);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Mudei de ideia sobre o aceite."));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var final = await LerEntregaAsync(factory, idEntregaFinal);
        Assert.Equal(StatusEntrega.Pendente, final.Status);
        Assert.Null(final.Feedback);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task AprovarEntrega_ComTccAprovado_EhPermitido()
    {
        // Contrapartida positiva de D9: Aprovado e EmAndamento são os dois estados válidos.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila, statusTcc: StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarParcial, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StatusEntrega.Aprovada, (await LerEntregaAsync(factory, idEntregaParcial)).Status);
    }

    // ── Guarda de vínculo e de papel ──────────────────────────────────────────────────────

    [Fact]
    public async Task AprovarEntrega_ComoProfessorQueNaoEhOOrientador_RetornaNotFound_ENaoAltera()
    {
        // Asserção de segurança do PR: ser Professor não basta, tem que ser O orientador
        // daquele TCC. 404 (e não 403) é deliberado — mesma semântica de RegistrarFeedback,
        // que não confirma a existência da entrega para quem não tem vínculo.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOutroProfessor, "Professor");

        var response = await client.PostAsync(RotaAprovarFinal, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task RejeitarEntrega_ComoProfessorQueNaoEhOOrientador_RetornaNotFound_ENaoAltera()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOutroProfessor, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Não gostei do trabalho alheio."));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var final = await LerEntregaAsync(factory, idEntregaFinal);
        Assert.Equal(StatusEntrega.Pendente, final.Status);
        Assert.Null(final.Feedback);
        Assert.Empty(fila.Mensagens);
    }

    [Theory]
    [InlineData("Aluno", idAluno)]
    [InlineData("Coordenador", idCoordenador)]
    public async Task AprovarEntrega_ComPapelDiferenteDeProfessor_RetornaForbidden(string papel, int idUsuario)
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idUsuario, papel);

        var response = await client.PostAsync(RotaAprovarFinal, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
    }

    [Theory]
    [InlineData("Aluno", idAluno)]
    [InlineData("Coordenador", idCoordenador)]
    public async Task RejeitarEntrega_ComPapelDiferenteDeProfessor_RetornaForbidden(string papel, int idUsuario)
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idUsuario, papel);

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Quero rejeitar minha própria entrega."));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StatusEntrega.Pendente, (await LerEntregaAsync(factory, idEntregaFinal)).Status);
    }

    [Fact]
    public async Task AprovarEntrega_EntregaInexistente_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync("/api/orientador/entregas/9999/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task RejeitarEntrega_EntregaInexistente_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync("/api/orientador/entregas/9999/rejeitar", Motivo("Motivo qualquer."));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }

    // ── D11: notificação ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RejeitarEntregaFinal_EnfileiraEmailComOAvisoDeReenvio()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        // Motivo sem acentuação de propósito: o corpo do e-mail passa por
        // WebUtility.HtmlEncode, que converte acentuados em entidades numéricas.
        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo("Faltam os resultados do capitulo 4."));
        response.EnsureSuccessStatusCode();

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Entrega rejeitada pelo orientador", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
        Assert.Contains("Faltam os resultados do capitulo 4.", msg.CorpoHtml, StringComparison.Ordinal);
        // Bloco extra que só existe quando a rejeitada é a Final — é o que avisa o aluno de que
        // ele precisa (e pode) reenviar.
        Assert.Contains("O ciclo de entregas foi reaberto.", msg.CorpoHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(fila.Mensagens, m => m.Assunto == "Feedback registrado na sua entrega");
    }

    [Fact]
    public async Task RejeitarEntregaParcial_EnfileiraEmailSemOAvisoDeReenvio()
    {
        // Rejeitar Parcial não reabre nada; o e-mail não pode dizer que reabriu.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryPadraoAsync(fila);
        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarParcial, Motivo("Refaca a fundamentacao teorica."));
        response.EnsureSuccessStatusCode();

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Entrega rejeitada pelo orientador", msg.Assunto);
        Assert.Contains("Refaca a fundamentacao teorica.", msg.CorpoHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("ciclo de entregas foi reaberto", msg.CorpoHtml, StringComparison.OrdinalIgnoreCase);
        // O placeholder precisa ter sido substituído por vazio, não deixado cru no corpo.
        Assert.DoesNotContain("{{BlocoReenvio}}", msg.CorpoHtml, StringComparison.Ordinal);
    }

    // ── D10: auditoria ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AprovarEntrega_RegistraLogDeAuditoriaComOsIdsEOVeredito()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        await SemearAsync(factory);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAprovarFinal, null);
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Veredito registrado pelo Professor: Aprovada", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"EntregaId: {idEntregaFinal}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"TccId: {idTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {idAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"OrientadorId: {idOrientador}", mensagem, StringComparison.Ordinal);
        Assert.Contains("TipoEntrega: Final", mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejeitarEntrega_LogDeAuditoria_ContemOsIdsMasNuncaOTextoDoMotivo()
    {
        // Mesmo guard de PII de CoordenadorController_RejeitarProposta_Tests (achado A09-2): o
        // motivo é texto livre do professor sobre o trabalho do aluno e não pode vazar em log.
        const string motivoDistintivo = "MOTIVO-SECRETO-QUE-NAO-PODE-VAZAR-NO-LOG-4d7b2e";

        using var factory = new ConfiguracaoCustomizadaApiFactory();
        await SemearAsync(factory);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsJsonAsync(RotaRejeitarFinal, Motivo(motivoDistintivo));
        response.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(
            logs,
            e => e.RenderMessage().Contains("Veredito registrado pelo Professor: Rejeitada", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"EntregaId: {idEntregaFinal}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"TccId: {idTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {idAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"OrientadorId: {idOrientador}", mensagem, StringComparison.Ordinal);

        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(motivoDistintivo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DarAceiteFinal_RegistraLogDeAuditoria()
    {
        // P-03: o endpoint não tinha trilha nenhuma antes desta issue, e é justamente ele que
        // consome o veredito registrado pelos dois endpoints novos.
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        await SemearAsync(factory, statusFinal: StatusEntrega.Aprovada);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync(RotaAceiteFinal, null);
        response.EnsureSuccessStatusCode();

        var entrada = Assert.Single(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains("Aceite final concedido pelo Professor", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"TccId: {idTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {idAluno}", mensagem, StringComparison.Ordinal);
        // Achado R4 do qa-agent (#81): trava explicitamente que OrientadorId é renderizado
        // como int (sem aspas), não a claim string relida — a asserção anterior
        // (Contains(id.ToString()) + Contains("OrientadorId:") separados) não distinguia
        // "OrientadorId: 30" de "OrientadorId: \"30\"", já que "30" é substring de "\"30\"".
        // Ver achado A09-1 da revisão de segurança.
        Assert.Contains($"OrientadorId: {idOrientador}", mensagem, StringComparison.Ordinal);
        Assert.DoesNotContain($"OrientadorId: \"{idOrientador}\"", mensagem, StringComparison.Ordinal);
    }
}
