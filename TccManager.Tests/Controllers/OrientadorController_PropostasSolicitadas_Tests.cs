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
/// Issue #112 (D1-D5) — as três ações novas do Professor sobre propostas que o solicitam:
/// GET/PUT api/orientador/propostas-solicitadas[...]. Cobre RBAC por vínculo (RNF01/D1),
/// concorrência otimista (RF06/RNF02/D5), auditoria (RF07/D10) e paridade com o fluxo
/// equivalente do Coordenador (D3).
/// </summary>
public class OrientadorController_PropostasSolicitadas_Tests
{
    private const int IdAluno = 10;
    private const int IdProfessorSolicitado = 20;
    private const int IdOutroProfessor = 21;
    private const int IdCoordenador = 30;
    private const int IdTcc = 1;

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

    private static async Task<FactoryComFilaFake> FactoryComPropostaAsync(
        FakeEmailQueue fila, StatusTcc status = StatusTcc.Pendente, int? orientadorSolicitadoId = IdProfessorSolicitado)
    {
        var factory = new FactoryComFilaFake(fila);
        using var ctx = factory.CriarContextoDireto();
        ctx.Usuarios.AddRange(
            NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdProfessorSolicitado, "Professor Solicitado", "prof@teste.com", TipoUsuario.Professor),
            NovoUsuario(IdOutroProfessor, "Outro Professor", "outro@teste.com", TipoUsuario.Professor),
            NovoUsuario(IdCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));
        ctx.Tccs.Add(new Tcc
        {
            Id = IdTcc,
            Titulo = "TCC de Teste",
            Resumo = "Resumo da proposta submetida pelo aluno.",
            AlunoId = IdAluno,
            OrientadorSolicitadoId = orientadorSolicitadoId,
            Status = status,
            DataCriacao = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
        return factory;
    }

    // ── GetPropostasSolicitadas (D1) ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPropostasSolicitadas_ListaApenasPropostaDoProprioVinculo()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.GetAsync("/api/orientador/propostas-solicitadas");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<TccResumoDto>>();

        Assert.NotNull(pagina);
        var item = Assert.Single(pagina!.Items);
        Assert.Equal(IdTcc, item.Id);
        Assert.Equal("Resumo da proposta submetida pelo aluno.", item.Resumo);
    }

    [Fact]
    public async Task GetPropostasSolicitadas_ProfessorSemVinculo_NaoVeAProposta()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdOutroProfessor, "Professor");
        var response = await client.GetAsync("/api/orientador/propostas-solicitadas");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<TccResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Empty(pagina!.Items);
    }

    [Fact]
    public async Task GetPropostasSolicitadas_PropostaJaAprovada_NaoAparece()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila, StatusTcc.Aprovado);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.GetAsync("/api/orientador/propostas-solicitadas");

        response.EnsureSuccessStatusCode();
        var pagina = await response.Content.ReadFromJsonAsync<PagedResult<TccResumoDto>>();

        Assert.NotNull(pagina);
        Assert.Empty(pagina!.Items);
    }

    // ── AprovarPropostaSolicitada (D1/D5) ──────────────────────────────────────────────────

    [Fact]
    public async Task AprovarPropostaSolicitada_ComVinculo_AprovaEEnfileiraNotificacaoParaOAluno()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null);

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.Id == IdTcc);
        Assert.Equal(StatusTcc.Aprovado, tcc.Status);
        Assert.Equal(IdProfessorSolicitado, tcc.OrientadorId);

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Proposta de TCC aprovada", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
    }

    [Fact]
    public async Task AprovarPropostaSolicitada_ProfessorSemVinculo_RetornaNotFound_ENaoAlteraEstado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdOutroProfessor, "Professor");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.Id == IdTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.OrientadorId);
    }

    [Fact]
    public async Task AprovarPropostaSolicitada_PropostaJaProcessada_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila, StatusTcc.Reprovado);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task AprovarPropostaSolicitada_IdInexistente_RetornaNotFound()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsync("/api/orientador/propostas-solicitadas/9999/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AprovarPropostaSolicitada_ComoAluno_RetornaForbidden()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AprovarPropostaSolicitada_RegistraLogDeAuditoriaComOsIds()
    {
        using var factory = await FactoryComPropostaComAuditoriaAsync();

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null);
        response.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(logs, e =>
            e.RenderMessage().Contains("Proposta aprovada pelo Professor solicitado", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"TccId: {IdTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {IdAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"OrientadorId: {IdProfessorSolicitado}", mensagem, StringComparison.Ordinal);
    }

    // ── RejeitarPropostaSolicitada (D1/D5/D9) ──────────────────────────────────────────────

    [Fact]
    public async Task RejeitarPropostaSolicitada_ComVinculo_RejeitaEEnfileiraNotificacaoParaOAluno()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        const string motivo = "Fora da minha linha de pesquisa.";

        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar", new RejeicaoDto { Motivo = motivo });

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.Id == IdTcc);
        Assert.Equal(StatusTcc.Reprovado, tcc.Status);
        Assert.Equal(motivo, tcc.MotivoRejeicao);
        Assert.Null(tcc.OrientadorId);

        var msg = Assert.Single(fila.Mensagens);
        Assert.Equal("Proposta de TCC rejeitada", msg.Assunto);
        Assert.Equal(new[] { "aluno@teste.com" }, msg.Destinatarios);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_ComScriptNoMotivo_PersisteSanitizadoSemTagScript()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");

        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar",
            new RejeicaoDto { Motivo = "Rejeitada <script>alert('xss')</script> por falta de aderência." });

        response.EnsureSuccessStatusCode();

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.Id == IdTcc);
        Assert.NotNull(tcc.MotivoRejeicao);
        Assert.DoesNotContain("<script", tcc.MotivoRejeicao, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rejeitada", tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_ProfessorSemVinculo_RetornaNotFound_ENaoAlteraEstado()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdOutroProfessor, "Professor");
        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar", new RejeicaoDto { Motivo = "Não é meu orientando." });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fila.Mensagens);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.SingleAsync(t => t.Id == IdTcc);
        Assert.Equal(StatusTcc.Pendente, tcc.Status);
        Assert.Null(tcc.MotivoRejeicao);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_MotivoVazio_RetornaBadRequest()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar", new RejeicaoDto { Motivo = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fila.Mensagens);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_ComoCoordenador_RetornaForbidden()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar", new RejeicaoDto { Motivo = "Motivo qualquer." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_LogDeAuditoria_ContemOsIdsMasNuncaOTextoDoMotivo()
    {
        const string motivoDistintivo = "MOTIVO-SECRETO-QUE-NAO-PODE-VAZAR-NO-LOG-112";

        using var factory = await FactoryComPropostaComAuditoriaAsync();

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{IdTcc}/rejeitar", new RejeicaoDto { Motivo = motivoDistintivo });

        response.EnsureSuccessStatusCode();

        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(logs, e =>
            e.RenderMessage().Contains("Proposta rejeitada pelo Professor solicitado", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"TccId: {IdTcc}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {IdAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"ProfessorId: {IdProfessorSolicitado}", mensagem, StringComparison.Ordinal);

        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains(motivoDistintivo, StringComparison.Ordinal));
    }

    // ── Concorrência otimista (RF06/RNF02/D5) ──────────────────────────────────────────────

    [Fact]
    public async Task AprovarPropostaSolicitada_ConflitoDeConcorrencia_Retorna409ENaoLanca500()
    {
        using var factory = new SaveChangesFalhaConcorrenciaPropostaApiFactory();
        int tccId;
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessorSolicitado, "Professor", "prof@teste.com", TipoUsuario.Professor));
            var tcc = new Tcc
            {
                Titulo = "TCC",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorSolicitadoId = IdProfessorSolicitado,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            };
            ctx.Tccs.Add(tcc);
            await ctx.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsync($"/api/orientador/propostas-solicitadas/{tccId}/aprovar", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RejeitarPropostaSolicitada_ConflitoDeConcorrencia_Retorna409ENaoLanca500()
    {
        using var factory = new SaveChangesFalhaConcorrenciaPropostaApiFactory();
        int tccId;
        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
                NovoUsuario(IdProfessorSolicitado, "Professor", "prof@teste.com", TipoUsuario.Professor));
            var tcc = new Tcc
            {
                Titulo = "TCC",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorSolicitadoId = IdProfessorSolicitado,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            };
            ctx.Tccs.Add(tcc);
            await ctx.SaveChangesAsync();
            tccId = tcc.Id;
        }

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PutAsJsonAsync(
            $"/api/orientador/propostas-solicitadas/{tccId}/rejeitar", new RejeicaoDto { Motivo = "Fora do escopo." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Paridade com o fluxo do Coordenador (D3) ───────────────────────────────────────────

    [Fact]
    public async Task AprovarPeloProfessor_EDesignarPeloCoordenador_ProduzemOMesmoEstadoFinal()
    {
        var filaProfessor = new FakeEmailQueue();
        using var factoryProfessor = await FactoryComPropostaAsync(filaProfessor);
        var clienteProfessor = factoryProfessor.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        await (await clienteProfessor.PutAsync($"/api/orientador/propostas-solicitadas/{IdTcc}/aprovar", null))
            .Content.ReadAsStringAsync();

        var filaCoordenador = new FakeEmailQueue();
        using var factoryCoordenador = await FactoryComPropostaAsync(filaCoordenador);
        var clienteCoordenador = factoryCoordenador.CreateClientAutenticado(IdCoordenador, "Coordenador");
        await clienteCoordenador.PutAsJsonAsync(
            $"/api/coordenador/propostas/{IdTcc}/designar-orientador",
            new DesignarOrientadorDto { OrientadorId = IdProfessorSolicitado });

        using var verificaProfessor = factoryProfessor.CriarContextoDireto();
        using var verificaCoordenador = factoryCoordenador.CriarContextoDireto();
        var tccViaProfessor = await verificaProfessor.Tccs.SingleAsync(t => t.Id == IdTcc);
        var tccViaCoordenador = await verificaCoordenador.Tccs.SingleAsync(t => t.Id == IdTcc);

        Assert.Equal(tccViaCoordenador.Status, tccViaProfessor.Status);
        Assert.Equal(tccViaCoordenador.OrientadorId, tccViaProfessor.OrientadorId);

        var assuntoProfessor = Assert.Single(filaProfessor.Mensagens).Assunto;
        var assuntoCoordenador = Assert.Single(filaCoordenador.Mensagens).Assunto;
        Assert.Equal(assuntoCoordenador, assuntoProfessor);
    }

    // ── Não-regressão: as rotas removidas por #76 continuam 404 (D2) ──────────────────────

    [Fact]
    public async Task RotaAntigaDeAprovacao_ContinuaNotFound_MesmoComORotaNovaExistindo()
    {
        var fila = new FakeEmailQueue();
        using var factory = await FactoryComPropostaAsync(fila);

        var client = factory.CreateClientAutenticado(IdProfessorSolicitado, "Professor");
        var response = await client.PostAsync($"/api/orientador/propostas/{IdTcc}/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<ConfiguracaoCustomizadaApiFactory> FactoryComPropostaComAuditoriaAsync()
    {
        var factory = new ConfiguracaoCustomizadaApiFactory();
        using var ctx = factory.CriarContextoDireto();
        ctx.Usuarios.AddRange(
            NovoUsuario(IdAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdProfessorSolicitado, "Professor Solicitado", "prof@teste.com", TipoUsuario.Professor));
        ctx.Tccs.Add(new Tcc
        {
            Id = IdTcc,
            Titulo = "TCC de Teste",
            Resumo = "Resumo da proposta.",
            AlunoId = IdAluno,
            OrientadorSolicitadoId = IdProfessorSolicitado,
            Status = StatusTcc.Pendente,
            DataCriacao = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
        return factory;
    }
}
