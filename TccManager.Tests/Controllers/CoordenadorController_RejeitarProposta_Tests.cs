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
/// Issue #76 (D7) — PUT api/coordenador/propostas/{id}/rejeitar.
///
/// A capacidade de rejeitar uma proposta existia em POST api/orientador/propostas/{id}/rejeitar,
/// sem nenhuma verificação de vínculo entre o Professor autenticado e a proposta (achado de RBAC).
/// Ela não foi eliminada do sistema: migrou de papel. Os casos de
/// OrientadorNotificacaoIntegracao_Tests.RejeitarProposta_RetornaOk_EEnfileiraNotificacaoParaOAluno
/// foram portados para cá (verbo POST -> PUT, rota do Orientador -> Coordenador, cliente
/// autenticado como "Professor" -> "Coordenador"); as asserções originais continuam válidas
/// (200, Status = Reprovado, e-mail "Proposta de TCC rejeitada" enfileirado para o aluno — RF8).
///
/// Acrescentado em relação ao teste antigo: MotivoRejeicao persistido e sanitizado, a guarda de
/// papel (403 para Professor) e as guardas de estado (404 para inexistente/já processada).
/// </summary>
public class CoordenadorController_RejeitarProposta_Tests
{
    private const int idAluno = 10;
    private const int idProfessor = 20;
    private const int idCoordenador = 30;
    private const int idTcc = 1;

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

    private static async Task<FactoryComFilaFake> FactoryComProposta(FakeEmailQueue fila, StatusTcc status = StatusTcc.Pendente)
    {
        var factory = new FactoryComFilaFake(fila);

        using var ctx = factory.CriarContextoDireto();
        ctx.Usuarios.AddRange(
            NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(idProfessor, "Professor", "prof@teste.com", TipoUsuario.Professor),
            NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));
        ctx.Tccs.Add(new Tcc
        {
            Id = idTcc,
            Titulo = "TCC de Teste",
            Resumo = "Resumo da proposta submetida pelo aluno.",
            AlunoId = idAluno,
            Status = status,
            DataCriacao = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        return factory;
    }

    // ── Caso portado de OrientadorNotificacaoIntegracao_Tests ─────────────────────────────

    [Fact]
    public async Task RejeitarProposta_RetornaOk_EEnfileiraNotificacaoParaOAluno()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        const string motivo = "Escopo muito amplo para o prazo.";

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = motivo });

        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // RF8 preservado: o gatilho da notificação mudou de dono, não deixou de existir.
        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Proposta de TCC rejeitada", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        // Asserção que o teste antigo não tinha.
        Assert.Equal(motivo, tcc.MotivoRejeicao);
        // Proposta pendente nunca tem orientador; rejeitar não pode inventar um.
        Assert.Null(tcc.OrientadorId);
    }

    // ── Motivo sanitizado (mesmo padrão de CoordenadorController_RegistrarResultadoBanca_Tests) ──

    [Fact]
    public async Task RejeitarProposta_ComScriptNoMotivo_PersisteSanitizadoSemTagScript()
    {
        // O motivo é campo livre e é exibido ao aluno em MeuTcc.razor. O controller persiste
        // _sanitizerService.Sanitizar(dto.Motivo), nunca o valor cru — mesma disciplina de
        // RegistrarResultadoBanca (issue #73, achado A10-1).
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar",
            new RejeicaoDto { Motivo = "Rejeitada <script>alert('xss')</script> por falta de metodologia." });

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);

        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.NotNull(tcc.MotivoRejeicao);
        Assert.DoesNotContain("<script", tcc.MotivoRejeicao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", tcc.MotivoRejeicao, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", tcc.MotivoRejeicao);
        Assert.DoesNotContain(">", tcc.MotivoRejeicao);
        // O texto legítimo em volta do payload continua no motivo (KeepChildNodes = true).
        Assert.Contains("Rejeitada", tcc.MotivoRejeicao);
        Assert.Contains("metodologia", tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarProposta_MotivoDentroDoLimiteCru_MasQueExpandeAoSanitizar_RetornaBadRequest()
    {
        // Achado A10-1: HtmlSanitizer codifica "&" em "&amp;" (5x maior) — 2000 "&" passam no
        // limite cru mas virariam 10000 caracteres na coluna nvarchar(2000). O
        // RejeicaoDtoValidator mede o valor JÁ sanitizado e o FluentValidationActionFilter
        // corta antes do corpo da ação rodar.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = new string('&', 2000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarProposta_MotivoVazio_RetornaBadRequest_ENaoAlteraEstado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
    }

    // ── Guardas de estado (espelham DesignarOrientador) ───────────────────────────────────

    [Fact]
    public async Task RejeitarProposta_IdInexistente_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            "/api/coordenador/propostas/9999/rejeitar", new RejeicaoDto { Motivo = "Qualquer motivo." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("não encontrada ou já processada", corpo);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task RejeitarProposta_PropostaJaProcessada_RetornaNotFound_ENaoReenviaNotificacao()
    {
        // Segunda rejeição da mesma proposta cai em 404 — comportamento deliberado e idêntico
        // ao de uma segunda designação de orientador.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        const string motivoOriginal = "Tema já defendido por outro aluno.";

        var primeira = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = motivoOriginal });
        primeira.EnsureSuccessStatusCode();

        var segunda = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "Outro motivo qualquer." });

        Assert.Equal(HttpStatusCode.NotFound, segunda.StatusCode);
        Assert.Single(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.Equal(motivoOriginal, tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarProposta_PropostaJaAprovada_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila, StatusTcc.Aprovado);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "Mudei de ideia." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Aprovado, tcc.Status);
    }

    // ── Contrapartida positiva da correção de RBAC: a capacidade existe, só para quem deve ──

    [Fact]
    public async Task RejeitarProposta_ComoProfessor_RetornaForbidden_ENaoAlteraEstado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idProfessor, "Professor");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "Não gostei do tema." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarProposta_ComoAluno_RetornaForbidden()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idAluno, "Aluno");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "Desisti." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == idTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
    }

    // ── P-05: o Coordenador precisa conseguir LER a proposta antes de rejeitá-la ──────────

    [Fact]
    public async Task GetPropostasPendentes_ProjetaResumoDaProposta()
    {
        // A projeção de GetPropostasPendentes não incluía Resumo (o Professor, que enxergava a
        // lista antes, recebia). Sem esse campo o Coordenador decidiria designar/rejeitar sem
        // poder ler o conteúdo da proposta na tela.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.GetAsync("/api/coordenador/propostas-pendentes");

        response.EnsureSuccessStatusCode();
        var pendentes = await response.Content.ReadFromJsonAsync<List<TccResumoDto>>();

        Assert.NotNull(pendentes);
        var proposta = Assert.Single(pendentes!);
        Assert.Equal("Resumo da proposta submetida pelo aluno.", proposta.Resumo);
        Assert.Equal("TCC de Teste", proposta.Titulo);
        Assert.Equal("Aluno", proposta.NomeAluno);
        Assert.Equal(StatusTcc.Pendente, proposta.Status);
    }

    [Fact]
    public async Task GetPropostasPendentes_ApósRejeicao_NaoListaMaisAProposta()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComProposta(fila);

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var rejeicao = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = "Fora do escopo do curso." });
        rejeicao.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/coordenador/propostas-pendentes");
        response.EnsureSuccessStatusCode();
        var pendentes = await response.Content.ReadFromJsonAsync<List<TccResumoDto>>();

        Assert.NotNull(pendentes);
        Assert.Empty(pendentes!);
    }

    // ── Achado A09-2 da revisão de segurança: trava de regressão para o log de auditoria ──

    [Fact]
    public async Task RejeitarProposta_LogDeAuditoria_ContemOsIdsMasNuncaOTextoDoMotivo()
    {
        // O controller já não loga o motivo (confirmado por leitura na revisão de segurança) —
        // este teste trava esse comportamento: um motivo propositalmente distintivo não pode
        // aparecer em NENHUMA linha de log emitida pelo host, nem por acidente futuro (ex.:
        // alguém "melhorando" a mensagem de auditoria para incluir o motivo "pra facilitar
        // debug" — a mesma classe de regressão que a issue #73 já preveniu em outros pontos).
        const string motivoDistintivo = "MOTIVO-SECRETO-QUE-NAO-PODE-VAZAR-NO-LOG-8f3a1c";

        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));
            ctx.Tccs.Add(new Tcc
            {
                Id = idTcc,
                Titulo = "TCC de Teste",
                Resumo = "Resumo da proposta.",
                AlunoId = idAluno,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/rejeitar", new RejeicaoDto { Motivo = motivoDistintivo });

        response.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        var entradaDeAuditoria = Assert.Single(logs, e =>
            e.RenderMessage().Contains("Proposta rejeitada pelo Coordenador", StringComparison.Ordinal));

        var mensagem = entradaDeAuditoria.RenderMessage();
        Assert.Contains($"TccId: {idTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {idAluno}", mensagem, StringComparison.Ordinal);
        // CoordenadorId vem de User.FindFirst(...).Value (string), e o renderizador padrão do
        // Serilog cerca argumentos de tipo string com aspas (diferente de TccId/AlunoId, que
        // são int) — mesmo comportamento já observado em ValidationFailureLoggingTests (#71).
        Assert.Contains(idCoordenador.ToString(), mensagem, StringComparison.Ordinal);
        Assert.Contains("CoordenadorId:", mensagem, StringComparison.Ordinal);

        // Núcleo do achado: o motivo não aparece em NENHUMA linha de log do host inteiro, não
        // só na linha de auditoria — cobre também o caso de outro ponto do pipeline vir a
        // logá-lo por engano (ex.: log de request, log de validação).
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(motivoDistintivo, StringComparison.Ordinal));
    }

    // ── Achado A09-1 da revisão de segurança: DesignarOrientador ganhou a mesma trilha ────

    [Fact]
    public async Task DesignarOrientador_RegistraLogDeAuditoriaComOsIds()
    {
        // Simétrico ao teste de auditoria de RejeitarProposta acima (achado A09-1): designar
        // orientador é igualmente uma decisão terminal sobre a proposta e precisa da mesma
        // trilha. Trava a regressão de o log voltar a ficar ausente nesta ação.
        const int idProfessorDesignado = 20;

        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(idProfessorDesignado, "Professor", "prof@teste.com", TipoUsuario.Professor),
                NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));
            ctx.Tccs.Add(new Tcc
            {
                Id = idTcc,
                Titulo = "TCC de Teste",
                Resumo = "Resumo da proposta.",
                AlunoId = idAluno,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync(
            $"/api/coordenador/propostas/{idTcc}/designar-orientador",
            new DesignarOrientadorDto { OrientadorId = idProfessorDesignado });

        response.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        var entradaDeAuditoria = Assert.Single(logs, e =>
            e.RenderMessage().Contains("Orientador designado pelo Coordenador", StringComparison.Ordinal));

        var mensagem = entradaDeAuditoria.RenderMessage();
        Assert.Contains($"TccId: {idTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {idAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"OrientadorId: {idProfessorDesignado}", mensagem, StringComparison.Ordinal);
        Assert.Contains(idCoordenador.ToString(), mensagem, StringComparison.Ordinal);
        Assert.Contains("CoordenadorId:", mensagem, StringComparison.Ordinal);
    }
}
