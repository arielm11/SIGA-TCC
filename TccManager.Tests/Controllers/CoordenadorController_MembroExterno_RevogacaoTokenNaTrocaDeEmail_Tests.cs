using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TccManager.Api.Services.Pdf;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #99 (achado A01-1): PUT /api/coordenador/membros-externos/{id} precisa revogar todos
/// os tokens de rascunho ativos do membro quando o e-mail é corrigido — senão quem controla o
/// endereço ANTIGO continua com acesso ao rascunho já enviado até a data da banca.
/// </summary>
public class CoordenadorController_MembroExterno_RevogacaoTokenNaTrocaDeEmail_Tests
{
    private const int IdCoordenador = 30;

    private static async Task<(TccApiFactory factory, int membroId, int bancaId)> PrepararMembroComBancaAsync()
    {
        var factory = new TccApiFactory();
        using var context = factory.CriarContextoDireto();

        context.Usuarios.Add(new Usuario { Id = IdCoordenador, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });

        var aluno = new Usuario { Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        context.Usuarios.Add(aluno);
        await context.SaveChangesAsync();

        var tcc = new Tcc { Titulo = "TCC", Resumo = "r", AlunoId = aluno.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
        context.Tccs.Add(tcc);
        await context.SaveChangesAsync();

        var banca = new Banca { TccId = tcc.Id, DataHora = DateTime.UtcNow.AddDays(3), Local = "Sala 1" };
        context.Banca.Add(banca);
        await context.SaveChangesAsync();

        var membro = new MembroExterno { Nome = "Externo", Email = "antigo@empresa.com", Instituicao = "Empresa" };
        context.MembrosExternos.Add(membro);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca.Id, MembroExternoId = membro.Id });
        await context.SaveChangesAsync();

        return (factory, membro.Id, banca.Id);
    }

    private static async Task<string> GerarTokenAsync(TccApiFactory factory, int bancaId, int membroId)
    {
        using var scope = factory.Services.CreateScope();
        var servico = scope.ServiceProvider.GetRequiredService<IRascunhoAtaTokenService>();
        return await servico.GerarTokenAsync(bancaId, membroId);
    }

    private static async Task<RascunhoTokenValidacaoStatus> ValidarTokenAsync(TccApiFactory factory, string token)
    {
        using var scope = factory.Services.CreateScope();
        var servico = scope.ServiceProvider.GetRequiredService<IRascunhoAtaTokenService>();
        return (await servico.ValidarAsync(token)).Status;
    }

    [Fact]
    public async Task AtualizarMembroExterno_TrocandoEmail_RevogaTokenDeRascunhoAtivo()
    {
        var (factory, membroId, bancaId) = await PrepararMembroComBancaAsync();
        using var _ = factory;
        var token = await GerarTokenAsync(factory, bancaId, membroId);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{membroId}", new MembroExternoDto
        {
            Id = membroId,
            Nome = "Externo",
            Email = "corrigido@empresa.com",
            Instituicao = "Empresa"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, await ValidarTokenAsync(factory, token));
    }

    [Fact]
    public async Task AtualizarMembroExterno_SemTrocarEmail_NaoRevogaToken()
    {
        // Contraprova: editar Nome/Instituicao sem tocar no e-mail não pode derrubar o token.
        var (factory, membroId, bancaId) = await PrepararMembroComBancaAsync();
        using var _ = factory;
        var token = await GerarTokenAsync(factory, bancaId, membroId);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{membroId}", new MembroExternoDto
        {
            Id = membroId,
            Nome = "Externo Renomeado",
            Email = "antigo@empresa.com", // inalterado
            Instituicao = "Empresa"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RascunhoTokenValidacaoStatus.Valido, await ValidarTokenAsync(factory, token));
    }

    [Fact]
    public async Task AtualizarMembroExterno_TrocandoEmail_RevogaTokensDeTodasAsBancasDoMembro()
    {
        var (factory, membroId, bancaA) = await PrepararMembroComBancaAsync();
        using var _ = factory;

        int bancaB;
        using (var context = factory.CriarContextoDireto())
        {
            var aluno2 = new Usuario { Nome = "Aluno 2", Email = "aluno2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
            context.Usuarios.Add(aluno2);
            await context.SaveChangesAsync();

            var tcc2 = new Tcc { Titulo = "TCC 2", Resumo = "r", AlunoId = aluno2.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
            context.Tccs.Add(tcc2);
            await context.SaveChangesAsync();

            var banca2 = new Banca { TccId = tcc2.Id, DataHora = DateTime.UtcNow.AddDays(5), Local = "Sala 2" };
            context.Banca.Add(banca2);
            await context.SaveChangesAsync();

            context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = banca2.Id, MembroExternoId = membroId });
            await context.SaveChangesAsync();
            bancaB = banca2.Id;
        }

        var tokenA = await GerarTokenAsync(factory, bancaA, membroId);
        var tokenB = await GerarTokenAsync(factory, bancaB, membroId);
        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var response = await client.PutAsJsonAsync($"/api/coordenador/membros-externos/{membroId}", new MembroExternoDto
        {
            Id = membroId,
            Nome = "Externo",
            Email = "corrigido@empresa.com",
            Instituicao = "Empresa"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, await ValidarTokenAsync(factory, tokenA));
        Assert.Equal(RascunhoTokenValidacaoStatus.Invalido, await ValidarTokenAsync(factory, tokenB));
    }
}
