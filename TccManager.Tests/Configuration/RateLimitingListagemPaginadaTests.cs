using System.Net;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #74 — política de rate limiting "listagem-paginada", aplicada aos 5 endpoints de
/// listagem paginada autenticados (CoordenadorController.GetProfessores/GetMembrosExternos/
/// GetBancasConcluidas, OrientadorController.GetDaboard, TccController.GetMinhasEntregas) mais
/// um sexto, UsuarioController.GetProfessores (achado F-01 da revisão de segurança: devolvia o
/// mesmo catálogo, com Email a mais e sem paginação, contornando a proteção dos outros 5).
/// FixedWindow, PermitLimit 60/60s (10/60s para requisição sem autenticação — achado F-02),
/// particionado por usuário autenticado (mesmo raciocínio de "geracao-pdf", achado A02-2:
/// partição por IP colapsaria a cota de uma rede inteira, ex. campus universitário atrás de
/// NAT/proxy, num único bucket compartilhado entre usuários diferentes).
/// </summary>
public class RateLimitingListagemPaginadaTests
{
    private const int PermitLimit = 60;
    private const int PermitLimitAnonimo = 10;
    private const int IdCoordenador = 1;
    private const int IdCoordenadorB = 2;

    [Fact]
    public async Task RequisicaoAcimaDoLimite_Retorna429ComRetryAfter()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        for (var i = 1; i <= PermitLimit; i++)
        {
            var permitida = await client.GetAsync("/api/coordenador/professores");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        var bloqueada = await client.GetAsync("/api/coordenador/professores");

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
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        for (var i = 1; i <= PermitLimit; i++)
        {
            var resp = await client.GetAsync("/api/coordenador/professores");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CotaEhCompartilhadaEntreEndpointsDaPoliticaParaOMesmoUsuario()
    {
        // A partição é por usuário, não por rota: consumir a cota num endpoint da política
        // também bloqueia outro endpoint da mesma política, para o MESMO usuário.
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        for (var i = 1; i <= PermitLimit; i++)
        {
            await client.GetAsync("/api/coordenador/professores");
        }

        var resposta = await client.GetAsync("/api/coordenador/membros-externos");

        Assert.Equal(HttpStatusCode.TooManyRequests, resposta.StatusCode);
    }

    [Fact]
    public async Task RequisicaoSemAutenticacao_UsaLimiteAnonimoMenorEBloqueiaAntesDe401()
    {
        // Achado F-02 da revisão de segurança: UseRateLimiter() roda antes de
        // UseAuthorization() em Program.cs, então uma requisição sem token válido ainda é
        // avaliada pelo limitador (cai no ramo "anon:{IP}") antes de ser rejeitada com 401.
        // O ramo anônimo usa um limite bem mais restrito que o de usuário autenticado — sem
        // isso, o fallback compartilharia a mesma cota generosa de 60/60s.
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClient();

        for (var i = 1; i <= PermitLimitAnonimo; i++)
        {
            var resposta = await client.GetAsync("/api/coordenador/professores");
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }

        var bloqueada = await client.GetAsync("/api/coordenador/professores");

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
    }

    [Fact]
    public async Task UsuarioControllerGetProfessores_TambemEhLimitado_MesmaCotaDoCoordenador()
    {
        // Achado F-01 da revisão de segurança: UsuarioController.GetProfessores devolve o
        // mesmo catálogo (com Email a mais, sem paginação), acessível a qualquer papel
        // autenticado — sem a mesma política aplicada aqui, a proteção do endpoint do
        // Coordenador seria contornável trivialmente por este caminho equivalente. A cota é
        // compartilhada (mesma política), então esgotar em um bloqueia o outro.
        using var factory = new WebRootIsolatedApiFactory();
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        for (var i = 1; i <= PermitLimit; i++)
        {
            await client.GetAsync("/api/usuario/professores");
        }

        var bloqueada = await client.GetAsync("/api/usuario/professores");

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
    }

    [Fact]
    public async Task CotaNaoEhCompartilhadaEntreUsuariosDiferentes()
    {
        using var factory = new WebRootIsolatedApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new TccManager.Shared.Models.Usuario
            {
                Id = IdCoordenadorB,
                Nome = "Coord B",
                Email = "coordb@teste.com",
                SenhaHash = "x",
                Tipo = TccManager.Shared.Enums.TipoUsuario.Coordenador,
                Ativo = true
            });
            await context.SaveChangesAsync();
        }

        var coordenadorA = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        for (var i = 1; i <= PermitLimit; i++)
        {
            await coordenadorA.GetAsync("/api/coordenador/professores");
        }

        var bloqueadaParaA = await coordenadorA.GetAsync("/api/coordenador/professores");
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueadaParaA.StatusCode);

        var coordenadorB = factory.CreateClientAutenticado(IdCoordenadorB, "Coordenador");
        var respostaParaB = await coordenadorB.GetAsync("/api/coordenador/professores");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, respostaParaB.StatusCode);
    }
}
