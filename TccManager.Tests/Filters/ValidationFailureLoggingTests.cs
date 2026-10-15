using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Filters;

/// <summary>
/// Issue #71, item 2 — log estruturado de falha de validação (400). Antes, nem a
/// checagem automática de ModelState do [ApiController] (DataAnnotations) nem o
/// FluentValidationActionFilter geravam qualquer log — o único jeito de perceber
/// abuso/fuzzing seria pela taxa de 400 do próprio Serilog request logging, sem
/// nenhum detalhe sobre quais campos falharam.
///
/// A correção envolve o IOptions&lt;ApiBehaviorOptions&gt;.InvalidModelStateResponseFactory
/// padrão (Program.cs) — o mesmo delegate usado tanto pelo filtro automático de
/// DataAnnotations quanto pelo FluentValidationActionFilter, então um só ponto cobre os
/// dois caminhos. Loga só os NOMES dos campos, nunca os valores submetidos.
/// </summary>
public class ValidationFailureLoggingTests
{
    private const int IdCoordenador = 1;

    [Fact]
    public async Task FalhaDeFluentValidation_LogaCamposQueFalharam_SemOsValoresSubmetidos()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new TccManager.Shared.Models.Usuario
            {
                Id = IdCoordenador,
                Nome = "Coordenador",
                Email = "coord@teste.com",
                SenhaHash = "x",
                Tipo = TccManager.Shared.Enums.TipoUsuario.Coordenador,
                Ativo = true
            });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");
        var valorSecreto = "email-que-nao-deveria-aparecer-no-log@evil.com";

        var resposta = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto
        {
            Nome = "",
            Email = valorSecreto, // formato válido, mas o Nome vazio já derruba a validação
            Instituicao = ""
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resposta.StatusCode);

        var logs = factory.LogsDoHost;
        var entradaDeValidacao = Assert.Single(logs, e =>
            e.RenderMessage().Contains("Falha de validação", StringComparison.Ordinal));

        var mensagem = entradaDeValidacao.RenderMessage();
        Assert.Contains("membros-externos", mensagem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(MembroExternoDto.Nome), mensagem, StringComparison.Ordinal);
        Assert.Contains(nameof(MembroExternoDto.Instituicao), mensagem, StringComparison.Ordinal);
        // Achado A09-3: log de validação sem ator não dá pra distinguir "quem" está
        // fuzzando/abusando — o ator do usuário autenticado (claim NameIdentifier) precisa
        // aparecer na mensagem.
        Assert.Contains("Ator:", mensagem, StringComparison.Ordinal);
        Assert.Contains(IdCoordenador.ToString(), mensagem, StringComparison.Ordinal);
        // O núcleo do achado: o valor submetido nunca aparece no log, só o nome do campo.
        Assert.DoesNotContain(valorSecreto, mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequisicaoValida_NaoGeraLogDeFalhaDeValidacao()
    {
        using var factory = new ConfiguracaoCustomizadaApiFactory();
        using (var context = factory.CriarContextoDireto())
        {
            context.Usuarios.Add(new TccManager.Shared.Models.Usuario
            {
                Id = IdCoordenador,
                Nome = "Coordenador",
                Email = "coord@teste.com",
                SenhaHash = "x",
                Tipo = TccManager.Shared.Enums.TipoUsuario.Coordenador,
                Ativo = true
            });
            await context.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(IdCoordenador, "Coordenador");

        var resposta = await client.PostAsJsonAsync("/api/coordenador/membros-externos", new MembroExternoDto
        {
            Nome = "Membro Válido",
            Email = "membro@universidade.edu.br",
            Instituicao = "Universidade Externa"
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, resposta.StatusCode);
        Assert.DoesNotContain(factory.LogsDoHost, e => e.RenderMessage().Contains("Falha de validação", StringComparison.Ordinal));
    }
}
