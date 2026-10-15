using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// Issue #81 — roteiro de aceitação da issue, ponta a ponta e pelo pipeline HTTP real:
/// aluno envia a Final → orientador rejeita → aluno envia uma NOVA Final (o que era impossível
/// antes desta issue) → orientador aprova → aceite final concedido.
///
/// É o único arquivo que junta os dois lados do mecanismo (upload do Aluno em
/// <c>TccController.EnviarEntrega</c> e veredito do Professor em <c>OrientadorController</c>),
/// que é onde a issue de fato acontece — cada um deles isolado passa sem provar a reabertura.
///
/// LIMITAÇÃO CONHECIDA (P-04): o provider EF Core InMemory não aplica índices únicos nem
/// filtros. A coexistência de 2 linhas <c>Final</c> (1 Rejeitada + 1 Pendente) é aceita aqui
/// independentemente de a migration ter mesmo trocado o filtro de
/// <c>[Tipo] = 1</c> para <c>[Tipo] = 1 AND [Status] &lt;&gt; 2</c>. Este teste prova o
/// comportamento de aplicação; o predicado do banco é travado por
/// <see cref="TccController_EntregaFinalUnica_Tests.DdlSqlServer_ContemIndiceUnicoFiltradoDeEntregaFinal"/>
/// e a verificação contra SQL Server real permanece pendência de QA.
/// </summary>
public class OrientadorController_CicloVeredictoFinal_Tests
{
    private const int idAluno = 10;
    private const int idOrientador = 20;
    private const int idCoordenador = 30;

    private static readonly byte[] ConteudoPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nentrega\n%%EOF");

    private sealed class FactoryComUploadEFilaFake : WebRootIsolatedApiFactory
    {
        private readonly FakeEmailQueue _fila;

        public FactoryComUploadEFilaFake(FakeEmailQueue fila) => _fila = fila;

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

    private static async Task<int> SemearTccAprovadoAsync(FactoryComUploadEFilaFake factory)
    {
        using var ctx = factory.CriarContextoDireto();

        ctx.Usuarios.AddRange(
            NovoUsuario(idAluno, "Aluno", "aluno@teste.com", TipoUsuario.Aluno),
            NovoUsuario(idOrientador, "Orientador", "orient@teste.com", TipoUsuario.Professor),
            NovoUsuario(idCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador));

        // Aprovado é o ponto de partida correto: TCC com orientador designado e ZERO entregas
        // (invariante fixada pela issue #82). O primeiro upload do ciclo abaixo é justamente o
        // que transiciona o TCC para EmAndamento (D2) — e o reenvio do passo 5 só é aceito
        // porque o gate de EnviarEntrega passou a aceitar os dois estados (D4/Grupo B). Os
        // endpoints de veredito aceitam Aprovado ou EmAndamento desde a #81 (D9).
        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = idAluno,
            OrientadorId = idOrientador,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };

        ctx.Tccs.Add(tcc);
        await ctx.SaveChangesAsync();

        return tcc.Id;
    }

    private static MultipartFormDataContent MontarFormFinal(string titulo) => new()
    {
        { new StringContent(titulo), "tituloEntrega" },
        { new StringContent(TipoEntrega.Final.ToString()), "tipo" },
        { new ByteArrayContent(ConteudoPdf), "arquivo", "final.pdf" }
    };

    private static async Task<int> IdDaFinalMaisRecenteAsync(FactoryComUploadEFilaFake factory, int tccId)
    {
        using var ctx = factory.CriarContextoDireto();
        return (await ctx.Entregas
            .AsNoTracking()
            .Where(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final)
            .OrderByDescending(e => e.Id)
            .FirstAsync()).Id;
    }

    [Fact]
    public async Task CicloCompleto_RejeitarFinal_ReenviarFinal_AprovarEDarAceiteFinal()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComUploadEFilaFake(fila);
        var tccId = await SemearTccAprovadoAsync(factory);

        var aluno = factory.CreateClientAutenticado(idAluno, "Aluno");
        var professor = factory.CreateClientAutenticado(idOrientador, "Professor");

        // 1) Aluno envia a versão final.
        var primeiroEnvio = await aluno.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"));
        Assert.Equal(HttpStatusCode.OK, primeiroEnvio.StatusCode);
        var idPrimeiraFinal = await IdDaFinalMaisRecenteAsync(factory, tccId);

        // 2) Antes de qualquer veredito, o aceite final já está bloqueado (D7).
        var aceitePrematuro = await professor.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);
        Assert.Equal(HttpStatusCode.BadRequest, aceitePrematuro.StatusCode);

        // 3) Orientador rejeita a final.
        var rejeicao = await professor.PostAsJsonAsync(
            $"/api/orientador/entregas/{idPrimeiraFinal}/rejeitar",
            new RejeicaoDto { Motivo = "Refazer a analise dos resultados." });
        Assert.Equal(HttpStatusCode.OK, rejeicao.StatusCode);

        // 4) Com a final rejeitada, o aceite continua bloqueado — agora com a mensagem que
        //    devolve a bola para o aluno.
        var aceitePosRejeicao = await professor.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);
        Assert.Equal(HttpStatusCode.BadRequest, aceitePosRejeicao.StatusCode);
        Assert.Contains(
            "Aguarde o novo envio do aluno",
            await aceitePosRejeicao.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 5) O CORE DA ISSUE: o aluno consegue enviar uma nova final (antes disto, 400).
        var reenvio = await aluno.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final Corrigida"));
        Assert.Equal(HttpStatusCode.OK, reenvio.StatusCode);
        var idSegundaFinal = await IdDaFinalMaisRecenteAsync(factory, tccId);
        Assert.NotEqual(idPrimeiraFinal, idSegundaFinal);

        // 6) Orientador aprova a nova final.
        var aprovacao = await professor.PostAsync($"/api/orientador/entregas/{idSegundaFinal}/aprovar", null);
        Assert.Equal(HttpStatusCode.OK, aprovacao.StatusCode);

        // 7) Aceite final concedido.
        var aceite = await professor.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);
        Assert.Equal(HttpStatusCode.OK, aceite.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        var tcc = await verifica.Tccs.AsNoTracking().FirstAsync(t => t.Id == tccId);
        Assert.Equal(StatusTcc.AguardandoDefesa, tcc.Status);

        var finais = await verifica.Entregas.AsNoTracking()
            .Where(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final)
            .ToListAsync();
        Assert.Equal(2, finais.Count);
        Assert.Equal(StatusEntrega.Rejeitada, finais.Single(e => e.Id == idPrimeiraFinal).Status);
        Assert.Equal(StatusEntrega.Aprovada, finais.Single(e => e.Id == idSegundaFinal).Status);

        // Trilha de e-mails do ciclo, na ordem em que o aluno os recebe.
        var assuntos = fila.Mensagens.Select(m => m.Assunto).ToList();
        Assert.Equal("Entrega rejeitada pelo orientador", assuntos[0]);
        Assert.Equal("Entrega aprovada pelo orientador", assuntos[1]);
        Assert.Contains("Aceite final concedido", assuntos);
    }

    [Fact]
    public async Task CicloCompleto_ApósOAceiteFinal_NenhumNovoVeredictoEhAceito()
    {
        // Fecho de D9: uma vez em AguardandoDefesa, nem a Final aprovada nem qualquer outra
        // entrega podem ter o veredito alterado (seria mexer no que já virou processo de banca).
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComUploadEFilaFake(fila);
        var tccId = await SemearTccAprovadoAsync(factory);

        var aluno = factory.CreateClientAutenticado(idAluno, "Aluno");
        var professor = factory.CreateClientAutenticado(idOrientador, "Professor");

        Assert.Equal(HttpStatusCode.OK, (await aluno.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);
        var idFinal = await IdDaFinalMaisRecenteAsync(factory, tccId);

        Assert.Equal(HttpStatusCode.OK, (await professor.PostAsync($"/api/orientador/entregas/{idFinal}/aprovar", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await professor.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null)).StatusCode);

        var novaRejeicao = await professor.PostAsJsonAsync(
            $"/api/orientador/entregas/{idFinal}/rejeitar",
            new RejeicaoDto { Motivo = "Mudei de ideia depois do aceite." });

        Assert.Equal(HttpStatusCode.BadRequest, novaRejeicao.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        var final = await verifica.Entregas.AsNoTracking().FirstAsync(e => e.Id == idFinal);
        Assert.Equal(StatusEntrega.Aprovada, final.Status);
        Assert.Null(final.Feedback);
    }

    [Fact]
    public async Task CicloPodeSeRepetir_DuasRejeicoesSeguidasDeUmaAprovacao()
    {
        // Produto: "o ciclo pode se repetir quantas vezes forem necessárias, sem limite". A
        // segunda rejeição é de uma LINHA nova (a primeira permanece Rejeitada e intocada) —
        // se D8 fosse aplicado por TCC em vez de por entrega, este teste ficaria vermelho.
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComUploadEFilaFake(fila);
        var tccId = await SemearTccAprovadoAsync(factory);

        var aluno = factory.CreateClientAutenticado(idAluno, "Aluno");
        var professor = factory.CreateClientAutenticado(idOrientador, "Professor");

        for (var tentativa = 1; tentativa <= 2; tentativa++)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await aluno.PostAsync("/api/tcc/entregas", MontarFormFinal($"Versão Final {tentativa}"))).StatusCode);

            var id = await IdDaFinalMaisRecenteAsync(factory, tccId);
            var rejeicao = await professor.PostAsJsonAsync(
                $"/api/orientador/entregas/{id}/rejeitar",
                new RejeicaoDto { Motivo = $"Correcoes pendentes na rodada {tentativa}." });

            Assert.Equal(HttpStatusCode.OK, rejeicao.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK,
            (await aluno.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final 3"))).StatusCode);
        var idTerceira = await IdDaFinalMaisRecenteAsync(factory, tccId);
        Assert.Equal(HttpStatusCode.OK, (await professor.PostAsync($"/api/orientador/entregas/{idTerceira}/aprovar", null)).StatusCode);

        var aceite = await professor.PostAsync($"/api/orientador/tcc/{tccId}/aceite-final", null);
        Assert.Equal(HttpStatusCode.OK, aceite.StatusCode);

        using var verifica = factory.CriarContextoDireto();
        var finais = await verifica.Entregas.AsNoTracking()
            .Where(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final)
            .ToListAsync();

        Assert.Equal(3, finais.Count);
        Assert.Equal(2, finais.Count(e => e.Status == StatusEntrega.Rejeitada));
        Assert.Single(finais, e => e.Status == StatusEntrega.Aprovada);
    }
}
