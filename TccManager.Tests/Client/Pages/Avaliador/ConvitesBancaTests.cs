using System.Reflection;
using TccManager.Client.Pages.Avaliador;
using Xunit;

namespace TccManager.Tests.Client.Pages.Avaliador;

/// <summary>
/// Testes da lógica C# pura de <see cref="ConvitesBanca"/> após a migração para Radzen (N3 Etapa 4).
///
/// Contexto: sem infraestrutura bUnit no projeto (gap pré-existente; RNF-04). O layout de cards,
/// os estados vazio/carregando, o spinner por linha (<c>IsBusy</c>) e os fluxos de HTTP/interop
/// (<c>CarregarConvites</c>, <c>BaixarAtaRascunho</c> com os erros 403/404/410 lidos do corpo) NÃO são
/// cobertos aqui — ver docs/testes/2026-07-14-migracao-radzen-blazor-etapa4.md.
///
/// O que É coberto: <c>UrlArquivo</c>, helper extraído do markup nesta etapa (antes era a expressão
/// <c>$"{Http.BaseAddress?.ToString().TrimEnd('/')}{convite.ArquivoFinalCaminho}"</c> inline no
/// <c>href</c>), que monta o link direto da versão final do TCC. É lógica de string pura: lê apenas
/// <c>HttpClient.BaseAddress</c>, sem executar nenhuma requisição.
/// </summary>
public class ConvitesBancaTests
{
    private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

    private static string InvocarUrlArquivo(string? baseAddress, string caminho)
    {
        var componente = new ConvitesBanca();
        var http = new HttpClient();
        if (baseAddress != null)
        {
            http.BaseAddress = new Uri(baseAddress);
        }

        var propriedadeHttp = typeof(ConvitesBanca).GetProperty("Http", Privados);
        if (propriedadeHttp != null)
        {
            propriedadeHttp.SetValue(componente, http);
        }
        else
        {
            var campoHttp = typeof(ConvitesBanca).GetField("Http", Privados)
                ?? throw new MissingMemberException(nameof(ConvitesBanca), "Http");
            campoHttp.SetValue(componente, http);
        }

        var metodo = typeof(ConvitesBanca).GetMethod("UrlArquivo", Privados)
            ?? throw new MissingMethodException(nameof(ConvitesBanca), "UrlArquivo");
        return (string)metodo.Invoke(componente, [caminho])!;
    }

    [Fact]
    public void UrlArquivo_BaseComBarraFinal_NaoDuplicaABarra()
    {
        // ArquivoFinalCaminho vem do backend como "/uploads/entregas/{arquivo}" (LocalStorageService).
        var url = InvocarUrlArquivo("https://localhost:7145/", "/uploads/entregas/versao_final.pdf");

        Assert.Equal("https://localhost:7145/uploads/entregas/versao_final.pdf", url);
    }

    [Fact]
    public void UrlArquivo_BaseSemBarraFinal_ConcatenaDireto()
    {
        var url = InvocarUrlArquivo("https://api.exemplo.br/tcc", "/uploads/entregas/versao_final.pdf");

        Assert.Equal("https://api.exemplo.br/tcc/uploads/entregas/versao_final.pdf", url);
    }

    [Fact]
    public void UrlArquivo_SemBaseAddress_RetornaApenasOCaminho()
    {
        // Guarda de null do operador ?. — não gera "https://null...".
        var url = InvocarUrlArquivo(null, "/uploads/entregas/versao_final.pdf");

        Assert.Equal("/uploads/entregas/versao_final.pdf", url);
    }

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
