using System.Reflection;
using TccManager.Client.Pages.Avaliador;
using Xunit;

namespace TccManager.Tests.Client.Pages.Avaliador;

/// <summary>
/// Testes da lógica C# pura de <see cref="ConvitesBanca"/> após a migração para Radzen (N3 Etapa 4).
///
/// Contexto: sem infraestrutura bUnit no projeto (gap pré-existente; RNF-04). O layout de cards,
/// os estados vazio/carregando, o spinner por linha (<c>IsBusy</c>) e os fluxos de HTTP/interop
/// (<c>CarregarConvites</c>, <c>BaixarAtaRascunho</c>, <c>BaixarArquivoFinal</c> com os erros
/// 403/404/410 lidos do corpo) NÃO são cobertos aqui — ver
/// docs/testes/2026-07-14-migracao-radzen-blazor-etapa4.md.
///
/// O helper <c>UrlArquivo</c> (link estático direto para o arquivo) foi removido na issue #69 —
/// o download da versão final passou a ser autenticado via <c>BaixarArquivoFinal</c>, que faz
/// requisição HTTP real e por isso está fora do escopo deste arquivo pelos mesmos critérios acima.
/// </summary>
public class ConvitesBancaTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void EstadoInicial_NenhumRascunhoEmDownload()
    {
        // bancaIdBaixandoRascunho controla o spinner por linha (IsBusy/Disabled do RadzenButton):
        // nasce nulo, ou seja, nenhum card em estado "Baixando...".
        var componente = new ConvitesBanca();

        var campo = typeof(ConvitesBanca).GetField("bancaIdBaixandoRascunho", Privados)
            ?? throw new MissingFieldException(nameof(ConvitesBanca), "bancaIdBaixandoRascunho");

        Assert.Null((int?)campo.GetValue(componente));
    }
}
