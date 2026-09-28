using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Achado de segurança (2026-09-28, descoberto durante a arquitetura do #112, sem relação
/// direta com a feature): <c>GetDetalhesTcc</c> faz <c>.Include(t => t.Aluno)</c> e devolve
/// <c>Ok(tcc)</c> — a entidade crua. Antes desta correção, <c>Usuario.SenhaHash</c> não tinha
/// nenhuma proteção contra serialização, então o hash bcrypt da senha do Aluno ia junto na
/// resposta JSON para qualquer Professor autorizado a ver aquele TCC. Corrigido com
/// <c>[JsonIgnore]</c> em <c>Usuario.SenhaHash</c> (defesa em profundidade, não uma projeção
/// pontual) — este teste prova o efeito no payload bruto, não confiando apenas no tipo estático
/// do retorno (o mesmo raciocínio já usado em
/// <c>UsuarioController_Seguranca_Tests.AssertPayloadSemVazamentoDeHash</c>).
/// </summary>
public class OrientadorController_GetDetalhesTcc_NaoVazaSenhaHash_Tests
{
    private const int IdAluno = 10;
    private const int IdOrientador = 20;

    [Fact]
    public async Task GetDetalhesTcc_PayloadBruto_NuncaContemSenhaHashDoAluno()
    {
        using var factory = new TccApiFactory();
        var hashDoAluno = BCrypt.Net.BCrypt.HashPassword("senha-super-secreta-do-aluno");

        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.AddRange(
                new Usuario { Id = IdAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = hashDoAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = IdOrientador, Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });

            context.Tccs.Add(new Tcc
            {
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = IdAluno,
                OrientadorId = IdOrientador,
                Status = StatusTcc.EmAndamento,
                DataCriacao = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var tccId = (await factory.CriarContextoDireto().Tccs.SingleAsync()).Id;
        var client = factory.CreateClientAutenticado(IdOrientador, "Professor");

        var response = await client.GetAsync($"/api/orientador/tcc/{tccId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();

        // Núcleo do achado: nem o hash exato, nem o prefixo do algoritmo bcrypt, nem o nome
        // do campo podem aparecer no payload.
        Assert.DoesNotContain(hashDoAluno, corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("$2a$", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("$2b$", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("senhaHash", corpo, StringComparison.OrdinalIgnoreCase);
    }
}
