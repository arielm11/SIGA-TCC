using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Services.Pdf;

/// <summary>
/// Testes de unidade do <see cref="RascunhoAtaTokenService"/> (N2 Etapa 2) diretamente
/// sobre um <see cref="AppDbContext"/> InMemory, sem o pipeline HTTP. Cobrem: geração
/// (CSPRNG hex 64, apenas o hash é persistido, ExpiresAtUtc = Banca.DataHora), validação
/// (Valido / Invalido para inexistente/revogado/expirado / ResultadoRegistrado para 410),
/// revogação isolada e a invariante "no máximo 1 token ativo por par".
///
/// LIMITAÇÃO conhecida (ver docs/dados, §3.1/§8): o provider EF Core InMemory NÃO aplica o
/// índice único filtrado UX_rascunho_ata_tokens_Banca_Membro_Ativo. A garantia de "1 token
/// ativo por par" é aqui exercitada apenas no nível de código (GerarTokenAsync revoga o
/// vigente antes de inserir). O enforcement declarativo no banco (falha de constraint em
/// corrida concorrente) só seria verificável com SQL Server real — fica sinalizado ao qa-agent.
/// </summary>
public class RascunhoAtaTokenServiceTests
{
    private static AppDbContext NovoContexto()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Banca> SemearBancaAsync(
        AppDbContext context,
        DateTime dataHora,
        decimal? notaFinal = null)
    {
        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var orientador = new Usuario { Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno, orientador);
        await context.SaveChangesAsync();

        var tcc = new Tcc
        {
            Titulo = "TCC Token",
            Resumo = "r",
            AlunoId = aluno.Id,
            OrientadorId = orientador.Id,
            Status = notaFinal == null ? StatusTcc.AguardandoDefesa : StatusTcc.Finalizado,
            DataCriacao = DateTime.UtcNow
        };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = dataHora, Local = "Sala 1", NotaFinal = notaFinal };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        return banca;
    }

    private static async Task<int> SemearMembroExternoAsync(AppDbContext context)
    {
        var membro = new MembroExterno { Nome = "Externo", Email = "ext@empresa.com", Instituicao = "Empresa" };
        context.MembrosExternos.Add(membro);
        await context.SaveChangesAsync();
        return membro.Id;
    }

    private static string CalcularHashEsperado(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor))).ToLowerInvariant();

    // ── Geração ───────────────────────────────────────────────────────

    [Fact]
    public async Task GerarTokenAsync_RetornaValorBrutoHex64Minusculo()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        var token = await servico.GerarTokenAsync(banca.Id, membroId);

        Assert.Equal(64, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public async Task GerarTokenAsync_DoisTokens_SaoDiferentes()
    {
        // CSPRNG (não Guid/sequencial): duas emissões geram valores distintos e imprevisíveis.
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        var token1 = await servico.GerarTokenAsync(banca.Id, membroId);
        var token2 = await servico.GerarTokenAsync(banca.Id, membroId);

        Assert.NotEqual(token1, token2);
    }

    [Fact]
    public async Task GerarTokenAsync_PersisteApenasOHash_NuncaOValorBruto()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        var tokenBruto = await servico.GerarTokenAsync(banca.Id, membroId);

        var linha = await context.RascunhoAtaTokens.SingleAsync();
        Assert.NotEqual(tokenBruto, linha.TokenHash);
        Assert.Equal(CalcularHashEsperado(tokenBruto), linha.TokenHash);
        // Nenhuma coluna carrega o valor bruto: a única representação persistida é o hash.
        Assert.DoesNotContain(context.RascunhoAtaTokens, t => t.TokenHash == tokenBruto);
    }

    [Fact]
    public async Task GerarTokenAsync_ExpiresAtUtc_IgualBancaDataHora()
    {
        using var context = NovoContexto();
        var dataHora = new DateTime(2027, 5, 10, 14, 30, 0, DateTimeKind.Utc);
        var banca = await SemearBancaAsync(context, dataHora);
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        await servico.GerarTokenAsync(banca.Id, membroId);

        var linha = await context.RascunhoAtaTokens.SingleAsync();
        Assert.Equal(dataHora, linha.ExpiresAtUtc);
        Assert.Equal(banca.Id, linha.BancaId);
        Assert.Equal(membroId, linha.MembroExternoId);
        Assert.Null(linha.RevokedAtUtc);
    }

    [Fact]
    public async Task GerarTokenAsync_BancaInexistente_Lanca()
    {
        using var context = NovoContexto();
        var servico = new RascunhoAtaTokenService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => servico.GerarTokenAsync(9999, 1));
    }

    // ── Validação ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidarAsync_TokenValido_RetornaValidoComBancaId()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);
        var token = await servico.GerarTokenAsync(banca.Id, membroId);

        var validacao = await servico.ValidarAsync(token);

        Assert.Equal(RascunhoTokenValidacaoStatus.Valido, validacao.Status);
        Assert.Equal(banca.Id, validacao.BancaId);
    }

    [Fact]
    public async Task ValidarAsync_TokenInexistente_RetornaInvalido()
    {
        using var context = NovoContexto();
        var servico = new RascunhoAtaTokenService(context);

        var validacao = await servico.ValidarAsync(new string('0', 64));

        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, validacao.Status);
    }

    [Fact]
    public async Task ValidarAsync_TokenRevogado_RetornaInvalido()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);
        var token = await servico.GerarTokenAsync(banca.Id, membroId);

        await servico.RevogarTokenAtualAsync(banca.Id, membroId);

        var validacao = await servico.ValidarAsync(token);
        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, validacao.Status);
    }

    [Fact]
    public async Task ValidarAsync_TokenExpiradoPorData_RetornaInvalido()
    {
        // Banca no passado: UtcNow >= DataHora → token expirado por data (resposta genérica).
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddMinutes(-5));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);
        var token = await servico.GerarTokenAsync(banca.Id, membroId);

        var validacao = await servico.ValidarAsync(token);

        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, validacao.Status);
    }

    [Fact]
    public async Task ValidarAsync_ResultadoRegistrado_RetornaResultadoRegistrado()
    {
        // NotaFinal preenchida + banca ainda no futuro (não expirada por data): 410 (RNF-03).
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3), notaFinal: 88m);
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);
        var token = await servico.GerarTokenAsync(banca.Id, membroId);

        var validacao = await servico.ValidarAsync(token);

        Assert.Equal(RascunhoTokenValidacaoStatus.ResultadoRegistrado, validacao.Status);
        Assert.Equal(banca.Id, validacao.BancaId);
    }

    // ── Revogação / invariante de token ativo ─────────────────────────

    [Fact]
    public async Task GerarTokenAsync_Reemissao_RevogaOAnterior_MantendoNoMaximoUmAtivo()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        var token1 = await servico.GerarTokenAsync(banca.Id, membroId);
        var token2 = await servico.GerarTokenAsync(banca.Id, membroId);

        // O antigo passa a INVÁLIDO (revogado, não deletado); o novo é o único válido.
        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, (await servico.ValidarAsync(token1)).Status);
        Assert.Equal(RascunhoTokenValidacaoStatus.Valido, (await servico.ValidarAsync(token2)).Status);

        var linhas = await context.RascunhoAtaTokens
            .Where(t => t.BancaId == banca.Id && t.MembroExternoId == membroId)
            .ToListAsync();
        Assert.Equal(2, linhas.Count); // ambos preservados no histórico
        Assert.Single(linhas, t => t.RevokedAtUtc == null); // no máximo 1 ativo
    }

    [Fact]
    public async Task RevogarTokenAtualAsync_SemTokenAtivo_NaoLanca()
    {
        using var context = NovoContexto();
        var banca = await SemearBancaAsync(context, DateTime.UtcNow.AddDays(3));
        var membroId = await SemearMembroExternoAsync(context);
        var servico = new RascunhoAtaTokenService(context);

        // Nenhum token gerado ainda: operação idempotente, sem exceção.
        await servico.RevogarTokenAtualAsync(banca.Id, membroId);

        Assert.Empty(context.RascunhoAtaTokens);
    }
}
