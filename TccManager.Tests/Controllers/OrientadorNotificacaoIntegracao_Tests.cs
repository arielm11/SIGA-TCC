using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TccManager.Api.Services.Email;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração dos pontos de disparo de notificação do OrientadorController (RegistrarFeedback e
/// DarAceiteFinal). Substitui o IEmailQueue real por um FakeEmailQueue e confirma, através do
/// pipeline HTTP completo, que (a) a resposta HTTP continua correta mesmo sem SMTP configurado e
/// (b) a notificação certa é enfileirada dentro do request. Mesmo padrão de
/// NotificacaoIntegracao_Tests (host único de TccApiFactory, InMemory).
///
/// Issue #76 (D1): os testes de AprovarProposta/RejeitarProposta saíram daqui.
/// - Aprovação não tem equivalente (aprovar virou "designar orientador", já coberto pelo fluxo do
///   Coordenador) — removido.
/// - Rejeição foi portada para CoordenadorController_RejeitarProposta_Tests (verbo PUT, rota
///   api/coordenador/propostas/{id}/rejeitar, papel Coordenador).
/// No lugar deles ficam os testes de contrato que travam a correção de RBAC: as duas rotas
/// removidas passam a ser 404 de roteamento, mesmo para um Professor autenticado, sem tocar no
/// estado do TCC e sem enfileirar e-mail nenhum.
/// </summary>
public class OrientadorNotificacaoIntegracao_Tests
{
    private const int idAluno = 10;
    private const int idOrientador = 20;
    private const int idCoordenador = 30;

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

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo, bool ativo = true)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = ativo };

    private static List<string> DestinatariosDaFila(FakeEmailQueue fila)
        => fila.Mensagens.Select(m => Assert.Single(m.Destinatarios)).ToList();

    // ── Issue #76 (D1): contrato das rotas removidas ──────────────────────────────────────
    // Sem estes dois testes a regressão de segurança não fica travada: bastaria alguém
    // recriar a ação no OrientadorController para o caminho sem verificação de vínculo voltar
    // a existir, sem nenhum teste vermelho.

    private async Task<FactoryComFilaFake> FactoryComPropostaPendenteDeOutroAluno(FakeEmailQueue fila)
    {
        var factory = new FactoryComFilaFake(fila);

        using var ctx = factory.CriarContextoDireto();
        ctx.Usuarios.AddRange(
            NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor));
        ctx.Tccs.Add(new Tcc
        {
            Id = 1,
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = idAluno,
            // Proposta pendente sem nenhum vínculo com o professor autenticado — exatamente o
            // caso que o endpoint removido aceitava.
            Status = StatusTcc.Pendente,
            DataCriacao = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        return factory;
    }

    [Fact]
    public async Task AprovarProposta_RotaRemovida_RetornaNotFound_ENaoAlteraOTcc()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaPendenteDeOutroAluno(fila);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync("/api/orientador/propostas/1/aprovar", null);

        // 404 do roteamento (a ação não existe mais), não um 404 de "recurso não encontrado".
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == 1);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.OrientadorId);
    }

    [Fact]
    public async Task RejeitarProposta_RotaRemovida_RetornaNotFound_ENaoAlteraOTcc()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaPendenteDeOutroAluno(fila);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");
        var dto = new RejeicaoDto { Motivo = "Escopo muito amplo para o prazo." };

        var response = await client.PostAsJsonAsync("/api/orientador/propostas/1/rejeitar", dto);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == 1);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
        Assert.Null(tcc.OrientadorId);
    }

    [Fact]
    public async Task GetDashboard_NaoDevolveMaisPropostasPendentes_NoCorpoJson()
    {
        // D2: a propriedade saiu do DTO — devolver PagedResult vazio seria um contrato que
        // mente ("não há propostas" em vez de "não é assunto deste papel"). Como a asserção
        // tipada não consegue mais expressar isso, a checagem é sobre o JSON cru.
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaPendenteDeOutroAluno(fila);

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.GetAsync("/api/orientador/dashboard");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("propostasPendentes", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orientandosAtivos", json, StringComparison.OrdinalIgnoreCase);

        var dashboard = JsonSerializer.Deserialize<DashboardOrientadorDto>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dashboard);
        Assert.Empty(dashboard!.OrientandosAtivos);
    }

    [Fact]
    public async Task RegistrarFeedback_RetornaOk_EEnfileiraNotificacaoParaOAluno()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor));
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            });
            ctx.Entregas.Add(new Entrega
            {
                Id = 7,
                TccId = 1,
                Titulo = "Entrega Parcial",
                ArquivoCaminho = "/x.pdf",
                Tipo = TipoEntrega.Parcial,
                DataEnvio = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");
        var dto = new FeedbackDto { Feedback = "Bom trabalho, ajuste as referências.", Nota = 8.5m };

        var response = await client.PostAsJsonAsync("/api/orientador/entregas/7/feedback", dto);

        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Feedback registrado na sua entrega", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);

        using var verifica = factory.CriarContextoDireto();
        var entrega = await verifica.Entregas.FirstAsync(e => e.Id == 7);
        Assert.Equal(8.5m, entrega.Nota);
    }

    [Fact]
    public async Task DarAceiteFinal_ComEntregaFinal_RetornaOk_EEnfileiraNotificacaoParaAlunoECoordenadores()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor),
                NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            });
            ctx.Entregas.Add(new Entrega
            {
                Id = 8,
                TccId = 1,
                Titulo = "Versão Final",
                ArquivoCaminho = "/final.pdf",
                Tipo = TipoEntrega.Final,
                DataEnvio = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync("/api/orientador/tcc/1/aceite-final", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.All(fila.Mensagens, m => Assert.Equal("Aceite final concedido", m.Assunto));
        var destinatarios = DestinatariosDaFila(fila);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("coord@teste.com", destinatarios);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.FirstAsync(t => t.Id == 1);
        Assert.Equal(StatusTcc.AguardandoDefesa, tcc.Status);
    }

    [Fact]
    public async Task DarAceiteFinal_SemEntregaFinal_RetornaBadRequest_ENaoEnfileira()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor));
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idOrientador, "Professor");

        var response = await client.PostAsync("/api/orientador/tcc/1/aceite-final", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }
}
