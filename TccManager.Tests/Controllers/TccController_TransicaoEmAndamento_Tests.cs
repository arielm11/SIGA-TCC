using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #82 — <c>Aprovado</c> e <c>EmAndamento</c> como dois estados REAIS de <c>Tcc.Status</c>,
/// com transição automática na primeira entrega (<c>POST api/tcc/entregas</c>).
///
/// O que estes testes travam, por decisão de arquitetura
/// (docs/arquitetura/2026-08-30-status-tcc-emandamento-nao-usado.md):
/// <list type="bullet">
/// <item><b>D2</b> — a primeira entrega transiciona <c>Aprovado → EmAndamento</c>, persistido no
/// MESMO <c>SaveChangesAsync</c> que insere a <c>Entrega</c> (sem estado intermediário
/// observável, sem <c>SaveChanges</c> extra).</item>
/// <item><b>D3</b> — qualquer <c>TipoEntrega</c> dispara (Parcial ou Final).</item>
/// <item><b>D4/Grupo B</b> — o gate de upload passou a aceitar <c>Aprovado || EmAndamento</c>. Sem
/// isso o próprio gatilho travaria o aluno já na SEGUNDA entrega — é o bug de produção que a
/// issue existe para evitar, e é o teste
/// <see cref="SegundaEntrega_ComTccJaEmAndamento_RetornaOkEMantemEmAndamento"/> que fica
/// vermelho se alguém implementar D2 sem D4.</item>
/// <item>Monotonicidade e ausência de vazamento: nenhum caminho de erro (validação, RN03, falha de
/// banco) deixa o status alterado.</item>
/// <item><b>D6</b> — a transição, única mudança de <c>Tcc.Status</c> sem autor humano, gera uma
/// linha de auditoria só com ids.</item>
/// </list>
///
/// O que NÃO é coberto aqui: o backfill (migration <c>BackfillStatusTccEmAndamento</c>). O harness
/// usa EF Core InMemory, que não aplica migrations — nenhum teste executa aquele SQL. É lacuna
/// conhecida e registrada (seção 11.4/P-01 da arquitetura); a verificação é manual, contra SQL
/// Server real.
/// </summary>
public class TccController_TransicaoEmAndamento_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private const string Rota = "/api/tcc/entregas";

    private static async Task<int> SemearTccAsync(
        TccApiFactory factory,
        StatusTcc status,
        bool comOrientador = true,
        int entregasParciaisPreExistentes = 0)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario
        {
            Id = IdAluno,
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
                Id = IdOrientador,
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
            AlunoId = IdAluno,
            OrientadorId = comOrientador ? IdOrientador : null,
            Status = status,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        for (var i = 0; i < entregasParciaisPreExistentes; i++)
        {
            context.Entregas.Add(new Entrega
            {
                TccId = tcc.Id,
                Titulo = $"Entrega pré-existente {i + 1}",
                ArquivoCaminho = "/uploads/entregas/fake.pdf",
                Tipo = TipoEntrega.Parcial,
                Status = StatusEntrega.Pendente,
                DataEnvio = DateTime.UtcNow.AddDays(-10 + i)
            });
        }

        if (entregasParciaisPreExistentes > 0)
            await context.SaveChangesAsync();

        return tcc.Id;
    }

    private static MultipartFormDataContent MontarForm(
        string titulo = "Entrega de Teste",
        TipoEntrega tipo = TipoEntrega.Parcial,
        string nomeArquivo = "entrega.pdf",
        long tamanhoBytes = 0)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(titulo), "tituloEntrega");
        form.Add(new StringContent(tipo.ToString()), "tipo");

        // Magic bytes de PDF; o conteúdo só precisa ser plausível para a extensão informada.
        var conteudo = new byte[Math.Max(tamanhoBytes, 5L)];
        conteudo[0] = 0x25; conteudo[1] = 0x50; conteudo[2] = 0x44; conteudo[3] = 0x46; conteudo[4] = 0x2D;

        var arquivo = new ByteArrayContent(conteudo);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(arquivo, "arquivo", nomeArquivo);

        return form;
    }

    private static async Task<StatusTcc> LerStatusAsync(TccApiFactory factory, int tccId)
    {
        using var context = factory.CriarContextoDireto();
        return (await context.Tccs.AsNoTracking().FirstAsync(t => t.Id == tccId)).Status;
    }

    private static async Task<int> ContarEntregasAsync(TccApiFactory factory, int tccId)
    {
        using var context = factory.CriarContextoDireto();
        return await context.Entregas.CountAsync(e => e.TccId == tccId);
    }

    // ── D2/D3: o gatilho ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TipoEntrega.Parcial)]
    [InlineData(TipoEntrega.Final)]
    public async Task PrimeiraEntrega_ComTccAprovado_TransicionaParaEmAndamento(TipoEntrega tipo)
    {
        // Núcleo da issue (D2 + D3): qualquer tipo de entrega tira o TCC de "orientador designado,
        // nada enviado" para "o aluno já começou". Orientador definido nos dois casos porque a
        // Final o exige (RN03) — o que se compara aqui é o efeito do TIPO, não do vínculo.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(tipo: tipo));

        resposta.EnsureSuccessStatusCode();
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
        Assert.Equal(1, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task PrimeiraEntrega_PersisteEntregaEStatusNoMesmoCommit()
    {
        // 4.1: a mutação de Tcc.Status e o INSERT da Entrega saem no mesmo SaveChangesAsync. O
        // observável, do lado de fora, é que nunca existe um dos dois sem o outro.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(titulo: "Capítulos 1 e 2"));
        resposta.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.AsNoTracking().FirstAsync(t => t.Id == tccId);
        var entrega = await context.Entregas.AsNoTracking().SingleAsync(e => e.TccId == tccId);

        Assert.Equal(StatusTcc.EmAndamento, tcc.Status);
        Assert.Equal("Capítulos 1 e 2", entrega.Titulo);
    }

    [Fact]
    public async Task PrimeiraEntrega_NaoAlteraOsDemaisCamposDoTcc()
    {
        // A transição é cirúrgica: só Status. Nada de OrientadorId, Titulo, Resumo ou DataCriacao.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm());
        resposta.EnsureSuccessStatusCode();

        using var context = factory.CriarContextoDireto();
        var tcc = await context.Tccs.AsNoTracking().FirstAsync(t => t.Id == tccId);

        Assert.Equal("TCC de Teste", tcc.Titulo);
        Assert.Equal("Resumo de teste", tcc.Resumo);
        Assert.Equal(IdOrientador, tcc.OrientadorId);
        Assert.Equal(IdAluno, tcc.AlunoId);
    }

    // ── D4/Grupo B: a segunda entrega (o bug que a issue fecha) ────────────────────────────

    [Fact]
    public async Task SegundaEntrega_ComTccJaEmAndamento_RetornaOkEMantemEmAndamento()
    {
        // TRIPWIRE do Grupo B. Antes do fix, o gate de EnviarEntrega comparava por igualdade
        // exata com Aprovado: a primeira entrega mudava o status e a segunda era recusada com
        // 400 ("Seu TCC precisa estar aprovado..."), travando o aluno. Ponta a ponta, com dois
        // uploads reais em sequência.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var primeira = await client.PostAsync(Rota, MontarForm(titulo: "Capítulo 1"));
        primeira.EnsureSuccessStatusCode();
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));

        var segunda = await client.PostAsync(Rota, MontarForm(titulo: "Capítulo 2"));

        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
        Assert.Equal(2, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task TerceiraEntrega_ContinuaAceita_ETransicaoNaoSeRepete()
    {
        // Monotonicidade (3.2): a transição acontece uma vez e as entregas seguintes não a
        // repetem nem quebram nada.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        foreach (var titulo in new[] { "Capítulo 1", "Capítulo 2", "Capítulo 3" })
        {
            var resposta = await client.PostAsync(Rota, MontarForm(titulo: titulo));
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        }

        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
        Assert.Equal(3, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaComTccSemeadoDiretamenteEmEmAndamento_EhAceita()
    {
        // Estado que o backfill produz (Aprovado com entregas → EmAndamento): o aluno que já
        // tinha entregas antes do deploy precisa conseguir enviar a próxima sem nenhum upload
        // prévio nesta sessão.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(
            factory, StatusTcc.EmAndamento, comOrientador: false, entregasParciaisPreExistentes: 2);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(titulo: "Capítulo 3"));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
        Assert.Equal(3, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaFinal_ComTccEmAndamento_EhAceita()
    {
        // Fecho natural do ciclo: o aluno que já enviou Parciais envia a Final com o TCC em
        // EmAndamento. Cobre a combinação (estado EmAndamento × TipoEntrega.Final), que os dois
        // casos anteriores não exercitam junta.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(
            factory, StatusTcc.EmAndamento, entregasParciaisPreExistentes: 1);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(titulo: "Versão Final", tipo: TipoEntrega.Final));

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
    }

    [Fact]
    public async Task PrimeiraEntrega_ComTccJaEmAndamentoSemNenhumaEntrega_NaoVoltaParaAprovado()
    {
        // Dado legado/inconsistente que o item 2 do backfill corrige (EmAndamento sem entrega).
        // Mesmo se ele sobreviver em algum ambiente, o upload NÃO pode "corrigir" o status para
        // trás: a transição é monotônica em uma única direção.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.EmAndamento, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm());

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusTcc.EmAndamento, await LerStatusAsync(factory, tccId));
    }

    // ── A transição não vaza em nenhum caminho de erro ─────────────────────────────────────

    [Theory]
    [InlineData(StatusTcc.Pendente)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    [InlineData(StatusTcc.Finalizado)]
    public async Task EntregaComTccForaDeAcompanhamento_RetornaBadRequest_ENaoAlteraOStatus(StatusTcc status)
    {
        // Complementa RF2 de TccController_EnviarEntrega_Tests com a asserção que a issue #82
        // acrescenta: além de recusar, o gate não pode deixar o status mexido.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, status);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm());

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(status, await LerStatusAsync(factory, tccId));
        Assert.Equal(0, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaComExtensaoInvalida_NaoTransicionaOStatus()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(nomeArquivo: "entrega.exe"));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(StatusTcc.Aprovado, await LerStatusAsync(factory, tccId));
        Assert.Equal(0, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaComTituloAcimaDoLimite_NaoTransicionaOStatus()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(titulo: new string('a', 201)));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(StatusTcc.Aprovado, await LerStatusAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaAcimaDoTamanhoMaximo_NaoTransicionaOStatus()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(
            Rota,
            MontarForm(tamanhoBytes: TccManager.Api.Configuration.UploadLimits.MaxArquivoUploadBytes + 1024));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(StatusTcc.Aprovado, await LerStatusAsync(factory, tccId));
        Assert.Equal(0, await ContarEntregasAsync(factory, tccId));
    }

    [Fact]
    public async Task EntregaFinalSemOrientador_RN03_NaoTransicionaOStatus()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(tipo: TipoEntrega.Final));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal(StatusTcc.Aprovado, await LerStatusAsync(factory, tccId));
    }

    // ── Atomicidade: falha de banco não deixa o status commitado ───────────────────────────

    [Theory]
    [InlineData(TipoEntrega.Parcial)]
    [InlineData(TipoEntrega.Final)]
    public async Task FalhaDeSaveChanges_NaoPersisteATransicaoDeStatus(TipoEntrega tipo)
    {
        // Sustenta a decisão de 4.1 (a mutação vive no change tracker até o ÚNICO SaveChanges):
        // se o INSERT da Entrega falha, o UPDATE de Tccs.Status também não é persistido — não
        // existe "TCC marcado como em andamento sem nenhuma entrega". Reusa a fixture de
        // interceptor já usada por TccController_CompensacaoUploadOrfao_Tests.
        using var factory = new SaveChangesFalhaEntregaApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(tipo: tipo));

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Equal(StatusTcc.Aprovado, await LerStatusAsync(factory, tccId));
        Assert.Equal(0, await ContarEntregasAsync(factory, tccId));
    }

    // ── D6: auditoria da transição ────────────────────────────────────────────────────────

    [Fact]
    public async Task PrimeiraEntrega_RegistraLogDeAuditoriaComOsIdsESemTextoLivre()
    {
        // D6/4.3: é a única transição de Tcc.Status sem autor humano identificável, então é a que
        // mais precisa de trilha. Disciplina de PII do projeto: só ids/metadados — nada de título
        // da entrega, nome ou e-mail do aluno.
        using var factory = new WebRootIsolatedComCapturaDeLogApiFactory();
        var tccId = await SemearTccAsync(factory, StatusTcc.Aprovado);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm(titulo: "TITULO-LIVRE-QUE-NAO-PODE-VAZAR"));
        resposta.EnsureSuccessStatusCode();

        int entregaId;
        using (var context = factory.CriarContextoDireto())
            entregaId = (await context.Entregas.AsNoTracking().SingleAsync(e => e.TccId == tccId)).Id;

        var logs = factory.LogsDoHost;
        var entrada = Assert.Single(
            logs,
            e => e.RenderMessage().Contains(
                "TCC transicionado automaticamente para EmAndamento", StringComparison.Ordinal));

        var mensagem = entrada.RenderMessage();
        Assert.Contains($"TccId: {tccId}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"AlunoId: {IdAluno}", mensagem, StringComparison.Ordinal);
        Assert.Contains($"EntregaId: {entregaId}", mensagem, StringComparison.Ordinal);
        Assert.Contains("Tipo: Parcial", mensagem, StringComparison.Ordinal);

        // Mesma trava do achado R4 do qa-agent na #81: os ids são renderizados como int, não
        // como a claim string relida ("AlunoId: 10", nunca "AlunoId: \"10\"").
        Assert.DoesNotContain($"AlunoId: \"{IdAluno}\"", mensagem, StringComparison.Ordinal);

        // Sem texto livre nem PII em NENHUM log emitido pela requisição.
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains("TITULO-LIVRE-QUE-NAO-PODE-VAZAR", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains("aluno@teste.com", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, e => e.RenderMessage().Contains("Aluno Teste", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SegundaEntrega_NaoRegistraNovamenteOLogDeTransicao()
    {
        // A trilha marca o EVENTO (o TCC começou), não cada upload. Contraprova de que o log é
        // condicionado a iniciouAcompanhamento, e não emitido incondicionalmente.
        using var factory = new WebRootIsolatedComCapturaDeLogApiFactory();
        await SemearTccAsync(factory, StatusTcc.Aprovado, comOrientador: false);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        (await client.PostAsync(Rota, MontarForm(titulo: "Capítulo 1"))).EnsureSuccessStatusCode();
        (await client.PostAsync(Rota, MontarForm(titulo: "Capítulo 2"))).EnsureSuccessStatusCode();

        Assert.Single(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains(
                "TCC transicionado automaticamente para EmAndamento", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EntregaRecusadaPeloGate_NaoRegistraLogDeTransicao()
    {
        using var factory = new WebRootIsolatedComCapturaDeLogApiFactory();
        await SemearTccAsync(factory, StatusTcc.AguardandoDefesa);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var resposta = await client.PostAsync(Rota, MontarForm());

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.DoesNotContain(
            factory.LogsDoHost,
            e => e.RenderMessage().Contains(
                "TCC transicionado automaticamente para EmAndamento", StringComparison.Ordinal));
    }
}
