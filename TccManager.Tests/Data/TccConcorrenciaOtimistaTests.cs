using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Data;

/// <summary>
/// Issue #113 (achado A06-1): documenta explicitamente uma limitação do harness, mesmo
/// espírito de <c>UsuarioController_EmailUnicoEUltimoAdmin_Tests.InsercaoDiretaDeEmailDuplicado_...</c>
/// (P-04) — o provider EF Core InMemory usado pela suíte NÃO gera um novo valor de
/// <c>Tcc.RowVersion</c> a cada <c>SaveChangesAsync</c> (isso é uma característica do tipo
/// <c>rowversion</c> do SQL Server, gerado pelo próprio banco); sem essa geração, dois
/// contextos que leem a mesma linha nunca divergem no RowVersion, e o InMemory não detecta o
/// conflito sozinho. A proteção real (constraint declarativa) só é verificável contra SQL
/// Server de verdade — fica sinalizado como pendência de QA, igual ao índice único.
///
/// O comportamento do CONTROLLER ao receber um <see cref="DbUpdateConcurrencyException"/> real
/// (tratamento como 409, sem corromper dado) é coberto por
/// <see cref="TccManager.Tests.Controllers.CoordenadorController_ConcorrenciaOtimistaPropostas_Tests"/>,
/// que fabrica a exceção via <c>ISaveChangesInterceptor</c> — mesmo padrão já usado para
/// simular falha de SaveChanges em outros pontos da suíte (SaveChangesFalhaEntregaApiFactory).
/// </summary>
public class TccConcorrenciaOtimistaTests
{
    [Fact]
    public async Task DoisContextosLeemAMesmaLinha_ProviderInMemory_NaoDetectaConflitoDeRowVersion_LimitacaoConhecida()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;

        using (var seed = new AppDbContext(options))
        {
            var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
            seed.Usuarios.Add(aluno);
            await seed.SaveChangesAsync();

            seed.Tccs.Add(new Tcc { Titulo = "TCC", Resumo = "r", AlunoId = aluno.Id, Status = StatusTcc.Pendente, DataCriacao = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        int tccId;
        using (var leitura = new AppDbContext(options))
            tccId = await leitura.Tccs.Select(t => t.Id).SingleAsync();

        using var contextA = new AppDbContext(options);
        using var contextB = new AppDbContext(options);

        var tccA = await contextA.Tccs.FindAsync(tccId);
        var tccB = await contextB.Tccs.FindAsync(tccId);

        tccA!.Status = StatusTcc.Aprovado;
        tccA.OrientadorId = 999;
        await contextA.SaveChangesAsync();

        tccB!.Status = StatusTcc.Reprovado;
        tccB.MotivoRejeicao = "Motivo qualquer.";

        // Núcleo da limitação: no SQL Server real, este SaveChanges lançaria
        // DbUpdateConcurrencyException (RowVersion mudou). No InMemory, não lança — nenhum
        // dos dois contextos jamais viu o RowVersion mudar, porque o provider não o gera.
        var excecao = await Record.ExceptionAsync(() => contextB.SaveChangesAsync());
        Assert.Null(excecao);
    }
}
