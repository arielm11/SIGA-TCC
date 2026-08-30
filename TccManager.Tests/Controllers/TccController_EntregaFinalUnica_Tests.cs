using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #69, item 3 — "no máximo uma entrega FINAL por TCC" (RN03).
/// Issue #81 (D2/D6) — a invariante deixou de ser "no máximo 1 Final" e passou a ser
/// "no máximo 1 Final NÃO REJEITADA": rejeitar a Final reabre o ciclo de entregas.
///
/// A defesa tem duas camadas:
/// 1) pre-check de aplicação em <c>TccController.EnviarEntrega</c>, que cobre o caso
///    sequencial e devolve 400 antes de gravar qualquer arquivo;
/// 2) índice único FILTRADO no banco (<c>UX_Entregas_TccId_Final</c>,
///    <c>WHERE [Tipo] = 1 AND [Status] &lt;&gt; 2</c>), backstop atômico para duas requisições
///    concorrentes que passem no pre-check ao mesmo tempo; nesse caso o
///    <c>catch (DbUpdateException)</c> devolve 409.
///
/// Os dois predicados precisam ser idênticos — é a invariante central da issue #81.
///
/// A camada 2 NÃO é exercitável neste harness: o provider EF Core InMemory não aplica índices
/// únicos nem filtros. Mesma limitação já documentada em
/// <see cref="UsuarioController_EmailUnicoEUltimoAdmin_Tests"/>, e tratada aqui do mesmo jeito:
/// o schema relacional real é conferido offline via <c>GenerateCreateScript</c> do provider
/// SQL Server, e a limitação do InMemory fica explícita num teste próprio em vez de escondida.
/// Consequência direta: os testes de "Final rejeitada libera novo envio" abaixo passariam mesmo
/// que a migration estivesse errada — eles provam o comportamento de APLICAÇÃO, não o
/// enforcement do banco. Quem prova o enforcement é o teste de DDL + a verificação manual
/// contra SQL Server real (P-04, pendência de QA).
/// </summary>
public class TccController_EntregaFinalUnica_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    private static readonly byte[] ConteudoPdf = Encoding.ASCII.GetBytes("%PDF-1.7\nentrega\n%%EOF");

    private static async Task<int> SemearTccAprovadoAsync(WebRootIsolatedApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        context.Usuarios.AddRange(
            new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orientador@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

        var tcc = new Tcc
        {
            Titulo = "TCC de Teste",
            Resumo = "Resumo",
            AlunoId = IdAluno,
            OrientadorId = IdOrientador,
            Status = StatusTcc.Aprovado,
            DataCriacao = DateTime.UtcNow
        };

        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        return tcc.Id;
    }

    private static MultipartFormDataContent MontarFormFinal(string titulo) => new()
    {
        { new StringContent(titulo), "tituloEntrega" },
        { new StringContent(TipoEntrega.Final.ToString()), "tipo" },
        { new ByteArrayContent(ConteudoPdf), "arquivo", "final.pdf" }
    };

    private static MultipartFormDataContent MontarFormParcial(string titulo) => new()
    {
        { new StringContent(titulo), "tituloEntrega" },
        { new StringContent(TipoEntrega.Parcial.ToString()), "tipo" },
        { new ByteArrayContent(ConteudoPdf), "arquivo", "parcial.pdf" }
    };

    /// <summary>
    /// Issue #81: aplica o veredito do orientador direto no banco (o objeto deste arquivo é o
    /// gate de <c>EnviarEntrega</c>, não o endpoint de veredito — esse tem arquivo próprio em
    /// <see cref="OrientadorController_VeredictoEntrega_Tests"/>).
    /// </summary>
    private static async Task DefinirStatusDaFinalAsync(WebRootIsolatedApiFactory factory, int tccId, StatusEntrega status)
    {
        using var context = factory.CriarContextoDireto();
        var final = await context.Entregas
            .Where(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final)
            .OrderByDescending(e => e.DataEnvio)
            .FirstAsync();

        final.Status = status;
        await context.SaveChangesAsync();
    }

    // ───────────── Camada 1: pre-check de aplicação (caminho sequencial) ─────────────

    [Fact]
    public async Task DuasEntregasFinaisSequenciais_ComPrimeiraPendenteDeVeredito_SegundaRetorna400ENaoPersisteNemGravaArquivo()
    {
        // Caminho síncrono, que não depende do índice do banco: o pre-check barra a segunda
        // submissão ANTES do upload (por isso continua havendo só 1 arquivo em disco).
        // Issue #81: o seed é explícito quanto ao veredito — a Final recém-enviada nasce
        // Pendente, e é justamente por NÃO estar Rejeitada que ela bloqueia.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        var primeira = await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"));
        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);

        using (var seed = factory.CriarContextoDireto())
        {
            var final = await seed.Entregas.SingleAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final);
            Assert.Equal(StatusEntrega.Pendente, final.Status);
        }

        var segunda = await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final 2"));

        // O pre-check devolve 400 (BadRequest); o 409 (Conflict) só ocorre no caminho de
        // corrida real, quando o índice único do banco dispara — ver testes de DDL abaixo.
        Assert.Equal(HttpStatusCode.BadRequest, segunda.StatusCode);
        Assert.Contains(
            "Sua versão FINAL está aguardando a avaliação do orientador.",
            await segunda.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(1, await context.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final));
        Assert.Single(Directory.GetFiles(factory.PastaEntregas));
    }

    [Fact]
    public async Task NovaEntregaAposFinalAprovada_RetornaBadRequestComMensagemDeCicloEncerrado()
    {
        // Issue #81 (D6): a Final aprovada é o único estado que encerra o ciclo de vez — a
        // mensagem é diferente da de "aguardando avaliação" para o aluno saber que não há mais
        // nada a fazer.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);
        await DefinirStatusDaFinalAsync(factory, tccId, StatusEntrega.Aprovada);

        var resposta = await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final 2"));

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Contains(
            "A versão FINAL já foi aprovada pelo orientador. O ciclo de entregas está encerrado.",
            await resposta.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(1, await context.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final));
    }

    [Fact]
    public async Task EntregaParcialAposFinal_TambemEhBloqueada()
    {
        // O pre-check encerra o ciclo inteiro de entregas, não só as FINAIS, enquanto houver
        // uma Final não rejeitada (aqui: Pendente, o default de quem acabou de enviar).
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);

        using var form = MontarFormParcial("Parcial atrasada");

        var resposta = await client.PostAsync("/api/tcc/entregas", form);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(1, await context.Entregas.CountAsync(e => e.TccId == tccId));
    }

    // ───────────── Issue #81: reabertura do ciclo após rejeição da Final ─────────────

    [Fact]
    public async Task FinalRejeitada_NovaFinalEhAceita_EPassamAExistirDuasLinhasFinais()
    {
        // O caso central da issue #81: com a Final Rejeitada, o pre-check deixa de bloquear e o
        // aluno reenvia. Passam a coexistir 2 linhas Final para o mesmo TCC (1 Rejeitada +
        // 1 Pendente) — o que só é legal porque o índice único do banco passou a filtrar
        // "AND [Status] <> 2".
        //
        // ATENÇÃO (limitação do harness): este teste passa no InMemory mesmo que a migration
        // esteja errada, porque o provider ignora índices filtrados. Ele prova o pre-check de
        // aplicação, não o enforcement do banco (ver o resumo da classe).
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);
        await DefinirStatusDaFinalAsync(factory, tccId, StatusEntrega.Rejeitada);

        var reenvio = await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final Corrigida"));

        Assert.Equal(HttpStatusCode.OK, reenvio.StatusCode);

        using var context = factory.CriarContextoDireto();
        var finais = await context.Entregas
            .Where(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final)
            .ToListAsync();

        Assert.Equal(2, finais.Count);
        Assert.Single(finais, e => e.Status == StatusEntrega.Rejeitada);
        Assert.Single(finais, e => e.Status == StatusEntrega.Pendente && e.Titulo == "Versão Final Corrigida");
        Assert.Equal(2, Directory.GetFiles(factory.PastaEntregas).Length);
    }

    [Fact]
    public async Task FinalRejeitada_EntregaParcialVoltaASerAceita()
    {
        // Reabrir o ciclo reabre o ciclo INTEIRO (o pre-check é um só): com a Final rejeitada o
        // aluno pode voltar a enviar Parciais, não só a nova Final.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);
        await DefinirStatusDaFinalAsync(factory, tccId, StatusEntrega.Rejeitada);

        using var form = MontarFormParcial("Correções do capítulo 3");
        var resposta = await client.PostAsync("/api/tcc/entregas", form);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(1, await context.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Parcial));
    }

    [Fact]
    public async Task FinalRejeitadaSeguidaDeNovaFinalPendente_VoltaABloquear()
    {
        // A reabertura não é permanente: assim que existe de novo uma Final não rejeitada, o
        // gate volta a valer (senão o aluno enviaria N finais pendentes de uma vez).
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final"))).StatusCode);
        await DefinirStatusDaFinalAsync(factory, tccId, StatusEntrega.Rejeitada);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final 2"))).StatusCode);

        var terceira = await client.PostAsync("/api/tcc/entregas", MontarFormFinal("Versão Final 3"));

        Assert.Equal(HttpStatusCode.BadRequest, terceira.StatusCode);
        Assert.Contains(
            "Sua versão FINAL está aguardando a avaliação do orientador.",
            await terceira.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var context = factory.CriarContextoDireto();
        Assert.Equal(2, await context.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final));
    }

    [Fact]
    public async Task VariasEntregasParciais_ContinuamPermitidas()
    {
        // Guarda de regressão do FILTRO do índice: a unicidade vale só para Tipo = Final.
        // Sem o "WHERE [Tipo] = 1" no banco real, a segunda parcial quebraria.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);
        var client = factory.CreateClientAutenticado(IdAluno, "Aluno");

        for (var i = 1; i <= 3; i++)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent($"Parcial {i}"), "tituloEntrega" },
                { new StringContent(TipoEntrega.Parcial.ToString()), "tipo" },
                { new ByteArrayContent(ConteudoPdf), "arquivo", $"parcial-{i}.pdf" }
            };

            var resposta = await client.PostAsync("/api/tcc/entregas", form);
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        }

        using var context = factory.CriarContextoDireto();
        Assert.Equal(3, await context.Entregas.CountAsync(e => e.TccId == tccId));
    }

    [Fact]
    public async Task EntregaFinalDeTccsDiferentes_NaoConflitam()
    {
        // O índice é por TccId: dois TCCs distintos podem ter, cada um, sua entrega FINAL.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);

        int outroTccId;
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = 11, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true });
            var outroTcc = new Tcc
            {
                Titulo = "Outro TCC",
                Resumo = "Resumo",
                AlunoId = 11,
                OrientadorId = IdOrientador,
                Status = StatusTcc.Aprovado,
                DataCriacao = DateTime.UtcNow
            };
            context.Tccs.Add(outroTcc);
            await context.SaveChangesAsync();
            outroTccId = outroTcc.Id;
        }

        var clienteA = factory.CreateClientAutenticado(IdAluno, "Aluno");
        var clienteB = factory.CreateClientAutenticado(11, "Aluno");

        Assert.Equal(HttpStatusCode.OK, (await clienteA.PostAsync("/api/tcc/entregas", MontarFormFinal("Final A"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clienteB.PostAsync("/api/tcc/entregas", MontarFormFinal("Final B"))).StatusCode);

        using var contexto = factory.CriarContextoDireto();
        Assert.Equal(1, await contexto.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final));
        Assert.Equal(1, await contexto.Entregas.CountAsync(e => e.TccId == outroTccId && e.Tipo == TipoEntrega.Final));
    }

    // ───────────── Camada 2: índice único filtrado no schema relacional ─────────────

    [Fact]
    public void ModeloEfCore_Entrega_DeclaraIndiceUnicoFiltradoEmTccId()
    {
        // Guarda de regressão da configuração em AppDbContext.OnModelCreating: se alguém
        // remover o IsUnique()/HasFilter(), a migration futura deixaria de reforçar a
        // invariante no banco e o backstop contra corrida sumiria em silêncio.
        using var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        var entidade = context.Model.FindEntityType(typeof(Entrega));
        Assert.NotNull(entidade);

        var indice = entidade!.GetIndexes()
            .SingleOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Entrega.TccId));

        Assert.NotNull(indice);
        Assert.True(indice!.IsUnique, "O índice de Entregas.TccId precisa ser UNIQUE (filtrado por Tipo = Final).");

        // Issue #81 (D2): o filtro precisa excluir as Finais REJEITADAS, senão a rejeição não
        // reabre nada — a segunda Final colidiria com a primeira no banco real.
        Assert.Equal("[Tipo] = 1 AND [Status] <> 2", indice.GetFilter());
    }

    [Fact]
    public void DdlSqlServer_ContemIndiceUnicoFiltradoDeEntregaFinal()
    {
        // O provider InMemory usado pela suíte NÃO aplica índices únicos nem filtros (ver
        // InsercaoDiretaDeDuasEntregasFinais_... abaixo). Aqui o schema relacional real é
        // conferido offline: gera-se o DDL do provider SQL Server, sem conexão com banco.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=servidor-inexistente;Database=TccManagerDdl;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new AppDbContext(options);
        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE UNIQUE INDEX [UX_Entregas_TccId_Final]", script, StringComparison.Ordinal);

        // O filtro é o que permite N entregas parciais e apenas 1 final NÃO REJEITADA por TCC.
        // TipoEntrega.Final == 1 e StatusEntrega.Rejeitada == 2 (enums persistidos como int,
        // sem HasConversion).
        //
        // Issue #81 (D2): este é o ÚNICO tripwire automatizado da mudança de índice — o
        // InMemory ignora filtros, então nenhum teste de comportamento consegue detectar uma
        // migration errada. Se o predicado voltar a ser só "[Tipo] = 1", a rejeição da Final
        // deixa de reabrir o ciclo em produção e apenas esta asserção fica vermelha.
        Assert.Contains("WHERE [Tipo] = 1 AND [Status] <> 2", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsercaoDiretaDeDuasEntregasFinais_ProviderInMemory_NaoAplicaIndiceUnico_LimitacaoConhecida()
    {
        // Documenta explicitamente a limitação do harness: o EF Core InMemory ignora índices
        // únicos e filtros, portanto a "segunda linha de defesa" (constraint do banco, que
        // alimenta o catch (DbUpdateException) => 409 Conflict do controller) NÃO é
        // exercitável aqui — quem protege nesta suíte é o pre-check de aplicação. Se a suíte
        // migrar para um provider relacional, este teste falha de propósito e deve virar
        // Assert.Throws<DbUpdateException>. Verificação em banco real: pendência de QA.
        using var factory = new WebRootIsolatedApiFactory();
        var tccId = await SemearTccAprovadoAsync(factory);

        using var context = factory.CriarContextoDireto();

        // Issue #81: as duas Finais são explicitamente Pendentes — ou seja, AMBAS entram no
        // predicado do índice filtrado ([Tipo] = 1 AND [Status] <> 2) e colidiriam num banco
        // relacional real. O InMemory aceita as duas assim mesmo, e é isso que o teste documenta.
        context.Entregas.AddRange(
            new Entrega { TccId = tccId, Titulo = "Final 1", ArquivoCaminho = "/uploads/entregas/a.pdf", Tipo = TipoEntrega.Final, Status = StatusEntrega.Pendente, DataEnvio = DateTime.UtcNow },
            new Entrega { TccId = tccId, Titulo = "Final 2", ArquivoCaminho = "/uploads/entregas/b.pdf", Tipo = TipoEntrega.Final, Status = StatusEntrega.Pendente, DataEnvio = DateTime.UtcNow });

        var excecao = await Record.ExceptionAsync(() => context.SaveChangesAsync());

        Assert.Null(excecao);
        Assert.Equal(2, await context.Entregas.CountAsync(e => e.TccId == tccId && e.Tipo == TipoEntrega.Final));
    }
}
