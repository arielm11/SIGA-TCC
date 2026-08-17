using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #64 — o <c>catch (Exception ex)</c> que envolve todo o <c>Program.cs</c> registrava
/// <c>Log.Fatal</c> e ENGOLIA a exceção: a aplicação morria silenciosamente com exit code 0,
/// o que faz orquestrador/serviço do SO acreditar que o encerramento foi normal. A correção
/// acrescentou <c>throw;</c> depois do log.
///
/// Limite deste teste: exit code é característica do PROCESSO e não é observável dentro do
/// runner do xUnit. O que dá para verificar em processo é a consequência direta do
/// <c>throw;</c> — a exceção original de inicialização atravessa o entry point em vez de
/// desaparecer. Sem o <c>throw;</c>, o <c>Main</c> retornaria normalmente e o
/// <c>WebApplicationFactory</c> falharia com um <c>InvalidOperationException</c> genérico
/// ("The entry point exited without ever building an IHost."), sem qualquer vestígio da causa
/// real — exatamente o sintoma que a issue descreve.
///
/// A verificação do exit code foi feita manualmente (ver relatório de testes da issue),
/// executando a API com <c>ASPNETCORE_URLS</c> inválida: o processo termina com exceção não
/// tratada e código de saída diferente de zero.
/// </summary>
public class StartupFailurePropagationTests
{
    private const string Marcador = "falha-simulada-de-inicializacao-issue-64";

    [Fact]
    public void FalhaDuranteAInicializacao_PropagaAExcecaoOriginal()
    {
        using var factory = new FactoryComFalhaNaInicializacao();

        var excecao = Assert.Throws<FalhaDeInicializacaoSimuladaException>(() => factory.CreateClient());

        Assert.Equal(Marcador, excecao.Message);
    }

    /// <summary>
    /// Injeta uma falha determinística no momento em que o <c>Program.cs</c> chama
    /// <c>builder.Build()</c> — ou seja, dentro do bloco <c>try</c> que termina no
    /// <c>catch</c>/<c>Log.Fatal</c>. É o análogo, em teste, de uma dependência que não
    /// consegue ser registrada/configurada na subida do host.
    /// </summary>
    private sealed class FactoryComFalhaNaInicializacao : TccApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices((IServiceCollection _) =>
                throw new FalhaDeInicializacaoSimuladaException(Marcador));
        }
    }

    private sealed class FalhaDeInicializacaoSimuladaException(string mensagem) : Exception(mensagem);
}
