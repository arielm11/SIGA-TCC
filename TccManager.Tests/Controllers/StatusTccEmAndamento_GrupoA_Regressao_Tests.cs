using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #82 — regressão do <b>Grupo A</b> (D5, seção 6 da arquitetura): os pontos que já tratavam
/// <c>Aprovado</c> e <c>EmAndamento</c> como equivalentes e que, por decisão explícita, <b>não</b>
/// tiveram uma linha de código alterada por esta issue.
///
/// Por que valem teste mesmo sem mudança de código: até esta issue, <c>EmAndamento</c> era um valor
/// <b>inatingível na prática</b> (nenhum caminho de produção o atribuía) — a segunda metade da
/// disjunção <c>Status == Aprovado || Status == EmAndamento</c> nunca era verdadeira em produção, e
/// esses pontos "acertavam por acidente". Com o gatilho automático (D2) e o backfill, o valor passa
/// a ser real e observável, então a equivalência precisa de tripwire: se alguém "simplificar" uma
/// dessas guardas para comparação exata com <c>Aprovado</c>, um TCC em andamento some do dashboard
/// do orientador, deixa de contar na carga do professor e some das estatísticas do coordenador.
///
/// Os dois endpoints de veredito da #81 (<c>aprovar</c>/<c>rejeitar</c>) já são exercitados com o
/// TCC em <c>EmAndamento</c> por <see cref="OrientadorController_VeredictoEntrega_Tests"/> (que
/// semeia <c>StatusTcc.EmAndamento</c> por padrão) — não são reduplicados aqui. O que este arquivo
/// acrescenta são os pontos sem nenhuma cobertura de integração anterior: os dois dashboards,
/// <c>CargaAtual</c> e o aceite final a partir de <c>EmAndamento</c>.
/// </summary>
public class StatusTccEmAndamento_GrupoA_Regressao_Tests
{
    private const int IdCoordenador = 1;
    private const int IdOrientador = 20;
    private const int IdAlunoAprovado = 11;
    private const int IdAlunoEmAndamento = 12;
    private const int IdAlunoPendente = 13;
    private const int IdAlunoFinalizado = 14;

    private const int IdTccAprovado = 101;
    private const int IdTccEmAndamento = 102;
    private const int IdTccPendente = 103;
    private const int IdTccFinalizado = 104;

    private static Usuario NovoUsuario(int id, string nome, string email, TipoUsuario tipo)
        => new() { Id = id, Nome = nome, Email = email, SenhaHash = "x", Tipo = tipo, Ativo = true };

    /// <summary>
    /// Semeia quatro TCCs do mesmo orientador, um por estado relevante: <c>Aprovado</c> e
    /// <c>EmAndamento</c> (ambos ativos) e <c>Pendente</c>/<c>Finalizado</c> (nenhum dos dois).
    /// Ter os quatro é o que permite afirmar tanto "EmAndamento conta" quanto "não conta demais".
    /// </summary>
    private static async Task SemearAsync(TccApiFactory factory)
    {
        using var ctx = factory.CriarContextoDireto();

        ctx.Usuarios.AddRange(
            NovoUsuario(IdCoordenador, "Coordenador", "coord@teste.com", TipoUsuario.Coordenador),
            NovoUsuario(IdOrientador, "Prof. Orientador", "orient@teste.com", TipoUsuario.Professor),
            NovoUsuario(IdAlunoAprovado, "Aluno Aprovado", "aprovado@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdAlunoEmAndamento, "Aluno Em Andamento", "andamento@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdAlunoPendente, "Aluno Pendente", "pendente@teste.com", TipoUsuario.Aluno),
            NovoUsuario(IdAlunoFinalizado, "Aluno Finalizado", "finalizado@teste.com", TipoUsuario.Aluno));

        ctx.Tccs.AddRange(
            new Tcc
            {
                Id = IdTccAprovado,
                Titulo = "TCC Aguardando 1a Entrega",
                Resumo = "Resumo",
                AlunoId = IdAlunoAprovado,
                OrientadorId = IdOrientador,
                Status = StatusTcc.Aprovado,
                DataCriacao = DateTime.UtcNow
            },
            new Tcc
            {
                Id = IdTccEmAndamento,
                Titulo = "TCC Em Andamento",
                Resumo = "Resumo",
                AlunoId = IdAlunoEmAndamento,
                OrientadorId = IdOrientador,
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            },
            new Tcc
            {
                Id = IdTccPendente,
                Titulo = "TCC Pendente",
                Resumo = "Resumo",
                AlunoId = IdAlunoPendente,
                Status = StatusTcc.Pendente,
                DataCriacao = DateTime.UtcNow
            },
            new Tcc
            {
                Id = IdTccFinalizado,
                Titulo = "TCC Finalizado",
                Resumo = "Resumo",
                AlunoId = IdAlunoFinalizado,
                OrientadorId = IdOrientador,
                Status = StatusTcc.Finalizado,
                DataCriacao = DateTime.UtcNow
            });

        ctx.Entregas.Add(new Entrega
        {
            Id = 501,
            TccId = IdTccEmAndamento,
            Titulo = "Capítulo 1",
            ArquivoCaminho = "/uploads/entregas/fake.pdf",
            Tipo = TipoEntrega.Parcial,
            Status = StatusEntrega.Pendente,
            DataEnvio = DateTime.UtcNow.AddDays(-5)
        });

        await ctx.SaveChangesAsync();
    }

    // ── OrientadorController.GetDaboard ───────────────────────────────────────────────────

    [Fact]
    public async Task DashboardDoOrientador_ListaTantoOTccAprovadoQuantoOEmAndamento()
    {
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var resposta = await client.GetAsync("/api/orientador/dashboard");
        resposta.EnsureSuccessStatusCode();

        var dashboard = await resposta.Content.ReadFromJsonAsync<DashboardOrientadorDto>();

        var ids = dashboard!.OrientandosAtivos.Select(t => t.Id).ToList();
        Assert.Contains(IdTccAprovado, ids);
        Assert.Contains(IdTccEmAndamento, ids);
        Assert.DoesNotContain(IdTccFinalizado, ids);
        Assert.Equal(2, ids.Count);

        // O status trafega no DTO: o valor 3 (EmAndamento) chega ao Client sem mudança de
        // contrato (seção 10 da arquitetura).
        Assert.Equal(
            StatusTcc.EmAndamento,
            dashboard.OrientandosAtivos.Single(t => t.Id == IdTccEmAndamento).Status);
    }

    // ── CoordenadorController.GetDashboardStats ───────────────────────────────────────────

    [Fact]
    public async Task DashboardStatsDoCoordenador_ContaEmAndamentoComoAtivo()
    {
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var resposta = await client.GetAsync("/api/coordenador/dashboard-stats");
        resposta.EnsureSuccessStatusCode();

        var stats = await resposta.Content.ReadFromJsonAsync<DashboardCoordenadorDto>();

        Assert.Equal(2, stats!.TotalAtivos); // Aprovado + EmAndamento
        Assert.Equal(1, stats.PropostasPendentes);
        Assert.Equal(1, stats.TccsConcluidos);
        Assert.Equal(0, stats.AguardandoBanca);
    }

    [Fact]
    public async Task DashboardStatsDoCoordenador_NaoMudaQuandoUmTccTransicionaDeAprovadoParaEmAndamento()
    {
        // Confirmação pontual da seção 6: a transição é INVISÍVEL para capacidade/contagem de
        // ativos — um TCC que era contado em Aprovado continua contado em EmAndamento.
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var antes = await (await client.GetAsync("/api/coordenador/dashboard-stats"))
            .Content.ReadFromJsonAsync<DashboardCoordenadorDto>();

        using (var ctx = factory.CriarContextoDireto())
        {
            var tcc = await ctx.Tccs.FirstAsync(t => t.Id == IdTccAprovado);
            tcc.Status = StatusTcc.EmAndamento;
            await ctx.SaveChangesAsync();
        }

        var depois = await (await client.GetAsync("/api/coordenador/dashboard-stats"))
            .Content.ReadFromJsonAsync<DashboardCoordenadorDto>();

        Assert.Equal(antes!.TotalAtivos, depois!.TotalAtivos);
    }

    // ── CoordenadorController.GetProfessores (CargaAtual) ─────────────────────────────────

    [Fact]
    public async Task CargaAtualDoProfessor_ContaAprovadoEEmAndamento_ENaoOsDemais()
    {
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var resposta = await client.GetAsync("/api/coordenador/professores");
        resposta.EnsureSuccessStatusCode();

        var pagina = await resposta.Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>();
        var professor = Assert.Single(pagina!.Items);

        // 2 ativos (Aprovado + EmAndamento); o Finalizado do mesmo orientador não conta.
        Assert.Equal(2, professor.CargaAtual);
    }

    [Fact]
    public async Task CargaAtualDoProfessor_NaoMudaComATransicaoParaEmAndamento()
    {
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var antes = (await (await client.GetAsync("/api/coordenador/professores"))
            .Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>())!.Items.Single().CargaAtual;

        using (var ctx = factory.CriarContextoDireto())
        {
            var tcc = await ctx.Tccs.FirstAsync(t => t.Id == IdTccAprovado);
            tcc.Status = StatusTcc.EmAndamento;
            await ctx.SaveChangesAsync();
        }

        var depois = (await (await client.GetAsync("/api/coordenador/professores"))
            .Content.ReadFromJsonAsync<PagedResult<ProfessorResumoDto>>())!.Items.Single().CargaAtual;

        Assert.Equal(antes, depois);
    }

    // ── OrientadorController.DarAceiteFinal ───────────────────────────────────────────────

    [Theory]
    [InlineData(StatusTcc.Aprovado)]
    [InlineData(StatusTcc.EmAndamento)]
    public async Task AceiteFinal_APartirDosDoisEstadosAtivos_LevaParaAguardandoDefesa(StatusTcc statusInicial)
    {
        // Aceite final é o outro ponto do Grupo A: os dois estados são pontos de partida válidos
        // e desembocam no mesmo AguardandoDefesa. Depois desta issue, EmAndamento é o caminho
        // real (só se chega à Final tendo enviado entrega).
        using var factory = new TccApiFactory();
        await SemearAsync(factory);

        using (var ctx = factory.CriarContextoDireto())
        {
            var tcc = await ctx.Tccs.FirstAsync(t => t.Id == IdTccEmAndamento);
            tcc.Status = statusInicial;
            ctx.Entregas.Add(new Entrega
            {
                Id = 502,
                TccId = IdTccEmAndamento,
                Titulo = "Versão Final",
                ArquivoCaminho = "/uploads/entregas/final.pdf",
                Tipo = TipoEntrega.Final,
                Status = StatusEntrega.Aprovada,
                DataEnvio = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var resposta = await client.PostAsync($"/api/orientador/tcc/{IdTccEmAndamento}/aceite-final", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var contexto = factory.CriarContextoDireto();
        var atualizado = await contexto.Tccs.AsNoTracking().FirstAsync(t => t.Id == IdTccEmAndamento);
        Assert.Equal(StatusTcc.AguardandoDefesa, atualizado.Status);
    }

    // ── Fluxos do Aluno que filtram por "Status != Reprovado" (RF-02) ─────────────────────

    [Fact]
    public async Task MeuTcc_DevolveOTccEmAndamentoParaOAluno()
    {
        // 4.4: os pontos do lado do Aluno já aceitavam EmAndamento (meu-tcc não filtra por status;
        // entregas/acompanhamentos/minha-banca filtram só por Status != Reprovado) — o valor novo
        // não pode sumir da tela do aluno.
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoEmAndamento, "Aluno");

        var resposta = await client.GetAsync("/api/tcc/meu-tcc");
        resposta.EnsureSuccessStatusCode();

        var tcc = await resposta.Content.ReadFromJsonAsync<Tcc>();
        Assert.Equal(IdTccEmAndamento, tcc!.Id);
        Assert.Equal(StatusTcc.EmAndamento, tcc.Status);
    }

    [Fact]
    public async Task MinhasEntregas_DevolveOHistoricoDoTccEmAndamento()
    {
        using var factory = new TccApiFactory();
        await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(IdAlunoEmAndamento, "Aluno");

        var resposta = await client.GetAsync("/api/tcc/entregas");
        resposta.EnsureSuccessStatusCode();

        var pagina = await resposta.Content.ReadFromJsonAsync<PagedResult<Entrega>>();
        Assert.Equal(501, Assert.Single(pagina!.Items).Id);
    }
}
