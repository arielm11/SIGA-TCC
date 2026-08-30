using TccManager.Shared.Enums;
using TccManager.Shared.Formatting;
using Xunit;

namespace TccManager.Tests.Shared.Formatting;

/// <summary>
/// Issue #82 (D8, seção 8.2 da arquitetura) — <see cref="StatusTccFormatter"/>.
///
/// Depois que <c>Aprovado</c> e <c>EmAndamento</c> viraram dois estados REAIS (antes desta issue
/// <c>EmAndamento</c> não era atribuído por nenhum caminho de produção), o rótulo exibido no badge
/// deixou de poder ser o <c>ToString()</c> cru do enum: "Aprovado" ao lado de uma lista de entregas
/// e "EmAndamento"/"AguardandoDefesa" grudados são exatamente o sintoma que originou a issue.
///
/// Molde de <see cref="NotaFormatterTests"/> (mesmo diretório <c>Shared/Formatting</c> e mesmo
/// precedente de formatação compartilhada entre API e Client). Os textos abaixo são contrato de
/// apresentação: se algum mudar, é decisão de produto e este teste é o ponto de revisão.
/// </summary>
public class StatusTccFormatterTests
{
    /// <summary>
    /// Rótulos esperados, um por valor do enum (8.2). Fica em um único lugar para que o teste de
    /// exaustividade abaixo consiga afirmar que NENHUM valor ficou de fora.
    /// </summary>
    private static readonly IReadOnlyDictionary<StatusTcc, string> RotulosEsperados =
        new Dictionary<StatusTcc, string>
        {
            [StatusTcc.Pendente] = "Em análise",
            [StatusTcc.Aprovado] = "Aguardando 1ª entrega",
            [StatusTcc.Reprovado] = "Reprovado",
            [StatusTcc.EmAndamento] = "Em andamento",
            [StatusTcc.AguardandoDefesa] = "Aguardando defesa",
            [StatusTcc.Finalizado] = "Finalizado"
        };

    [Theory]
    [InlineData(StatusTcc.Pendente, "Em análise")]
    [InlineData(StatusTcc.Aprovado, "Aguardando 1ª entrega")]
    [InlineData(StatusTcc.Reprovado, "Reprovado")]
    [InlineData(StatusTcc.EmAndamento, "Em andamento")]
    [InlineData(StatusTcc.AguardandoDefesa, "Aguardando defesa")]
    [InlineData(StatusTcc.Finalizado, "Finalizado")]
    public void Formatar_MapeiaCadaValorDoEnumParaOSeuRotulo(StatusTcc status, string esperado)
    {
        Assert.Equal(esperado, StatusTccFormatter.Formatar(status));
    }

    [Fact]
    public void Formatar_AprovadoEEmAndamento_ProduzemRotulosDistintos()
    {
        // O núcleo da issue: os dois estados precisam ser distinguíveis na tela. Enquanto
        // EmAndamento era inatingível, qualquer rótulo servia; agora não.
        Assert.NotEqual(
            StatusTccFormatter.Formatar(StatusTcc.Aprovado),
            StatusTccFormatter.Formatar(StatusTcc.EmAndamento));
    }

    [Fact]
    public void Formatar_CobreTodosOsValoresDoEnumSemCairNoFallback()
    {
        // Trava a decisão de 8.2 (mapear o enum INTEIRO, não só os dois valores em disputa): um
        // mapeamento parcial produziria vocabulário misto no mesmo badge ("Em andamento" ao lado
        // de "AguardandoDefesa"). Se um valor novo for adicionado a StatusTcc sem entrada no
        // switch do formatador, este teste falha — que é o comportamento desejado.
        foreach (var status in Enum.GetValues<StatusTcc>())
        {
            Assert.True(
                RotulosEsperados.ContainsKey(status),
                $"Valor {status} de StatusTcc sem rótulo esperado declarado neste teste.");

            Assert.Equal(RotulosEsperados[status], StatusTccFormatter.Formatar(status));
        }
    }

    [Theory]
    [InlineData(StatusTcc.EmAndamento)]
    [InlineData(StatusTcc.AguardandoDefesa)]
    public void Formatar_ValoresComNomeGrudado_NaoExibemOToStringCru(StatusTcc status)
    {
        // "EmAndamento"/"AguardandoDefesa" eram exibidos exatamente assim no badge antes desta
        // issue — é a parte visível da correção de D8.
        Assert.NotEqual(status.ToString(), StatusTccFormatter.Formatar(status));
    }

    [Fact]
    public void Formatar_ValorForaDoEnum_CaiNoFallbackSemLancar()
    {
        // Mesmo padrão defensivo de NotaFormatter: o enum é persistido como int puro (sem
        // HasConversion), então um valor inesperado vindo do banco não pode derrubar a tela.
        Assert.Equal("99", StatusTccFormatter.Formatar((StatusTcc)99));
    }
}
