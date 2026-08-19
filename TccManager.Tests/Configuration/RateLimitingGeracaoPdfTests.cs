using System.Net;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #69, item 8 — política de rate limiting "geracao-pdf", aplicada aos endpoints que
/// disparam geração de PDF sob demanda (AvaliadorController.GetAtaRascunhoPdf,
/// CoordenadorController.GetAtaPdf e GetAtaRascunhoPdf). FixedWindow, PermitLimit 20/60s —
/// mas, diferente de <see cref="RateLimitingRascunhoPublicoTests"/>, particionado por
/// **usuário autenticado** (Claim NameIdentifier), não por IP (achado A02-2, corrigido em
/// 2026-08-18: partição por IP colapsava a cota de uma rede inteira — ex. campus
/// universitário atrás de NAT/proxy — num único bucket compartilhado entre usuários
/// diferentes). UseAuthentication roda antes de UseRateLimiter em Program.cs para que
/// HttpContext.User já esteja populado quando a política particiona a requisição.
///
/// As requisições de consumo usam propositalmente um usuário SEM vínculo com a banca (403):
/// UseRateLimiter roda antes de UseAuthorization/endpoint, então a cota é consumida sem
/// gerar PDF de verdade — o teste mede o limitador, não o custo do PDF.
/// </summary>
public class RateLimitingGeracaoPdfTests
{
    private const int PermitLimit = 20;
    private const int IdProfessorSemVinculo = 21;

    private static async Task<int> SemearBancaAsync(TccApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        context.Usuarios.Add(aluno);
        context.Usuarios.Add(new Usuario { Id = IdProfessorSemVinculo, Nome = "Prof", Email = "prof@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
        await context.SaveChangesAsync();

        var tcc = new Tcc { Titulo = "TCC", Resumo = "r", AlunoId = aluno.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        return banca.Id;
    }

    [Fact]
    public async Task RequisicaoAcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = new TccApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessorSemVinculo, "Professor");
        var rota = $"/api/avaliador/banca/{bancaId}/ata-rascunho-pdf";

        for (var i = 1; i <= PermitLimit; i++)
        {
            var permitida = await client.GetAsync(rota);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        var bloqueada = await client.GetAsync(rota);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 deve conter o header Retry-After.");
        var retryAfter = bloqueada.Headers.GetValues("Retry-After").First();
        Assert.True(int.TryParse(retryAfter, out var segundos),
            $"Retry-After deveria ser um inteiro em segundos, mas foi '{retryAfter}'.");
        Assert.InRange(segundos, 1, 60);
    }

    [Fact]
    public async Task DentroDoLimite_NenhumaEhBloqueada()
    {
        using var factory = new TccApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var client = factory.CreateClientAutenticado(IdProfessorSemVinculo, "Professor");
        var rota = $"/api/avaliador/banca/{bancaId}/ata-rascunho-pdf";

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resp = await client.GetAsync(rota);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CotaEhCompartilhadaEntreEndpointsParaOMesmoUsuario()
    {
        // A partição é por usuário, não por rota: consumir a cota num endpoint da política
        // também bloqueia outro endpoint da mesma política, para o MESMO usuário.
        const int idCoordenador = 30;
        using var factory = new TccApiFactory();
        var bancaId = await SemearBancaAsync(factory);
        var coordenador = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        for (var i = 1; i <= PermitLimit; i++)
        {
            await coordenador.GetAsync($"/api/coordenador/banca/{bancaId}/ata-rascunho-pdf");
        }

        var resposta = await coordenador.GetAsync($"/api/coordenador/banca/{bancaId}/ata-pdf");

        Assert.Equal(HttpStatusCode.TooManyRequests, resposta.StatusCode);
    }

    [Fact]
    public async Task CotaNaoEhCompartilhadaEntreUsuariosDiferentes()
    {
        // Núcleo do achado A02-2: antes, a partição era por IP — em teste (mesmo cliente HTTP,
        // mesma máquina), dois usuários diferentes compartilhavam a mesma cota, reproduzindo
        // o cenário real de usuários atrás do mesmo NAT/proxy (ex.: rede de campus). Com a
        // partição por usuário autenticado, um Coordenador esgotar a própria cota não afeta
        // outro Coordenador (nem qualquer outro usuário), mesmo vindo do mesmo IP de origem.
        const int idCoordenadorA = 30;
        const int idCoordenadorB = 31;
        using var factory = new TccApiFactory();
        var bancaId = await SemearBancaAsync(factory);

        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new Usuario { Id = idCoordenadorB, Nome = "Coord B", Email = "coordb@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            await context.SaveChangesAsync();
        }

        var coordenadorA = factory.CreateClientAutenticado(idCoordenadorA, "Coordenador");
        for (var i = 1; i <= PermitLimit; i++)
        {
            await coordenadorA.GetAsync($"/api/coordenador/banca/{bancaId}/ata-rascunho-pdf");
        }

        var bloqueadaParaA = await coordenadorA.GetAsync($"/api/coordenador/banca/{bancaId}/ata-rascunho-pdf");
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueadaParaA.StatusCode);

        var coordenadorB = factory.CreateClientAutenticado(idCoordenadorB, "Coordenador");
        var respostaParaB = await coordenadorB.GetAsync($"/api/coordenador/banca/{bancaId}/ata-rascunho-pdf");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, respostaParaB.StatusCode);
    }
}
