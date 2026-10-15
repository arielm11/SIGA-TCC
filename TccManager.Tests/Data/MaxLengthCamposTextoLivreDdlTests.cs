using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using Xunit;

namespace TccManager.Tests.Data;

/// <summary>
/// Issue #73 — o provider InMemory usado pelo resto da suíte não aplica [MaxLength] como
/// restrição real de coluna (aceita qualquer tamanho de string sem erro), então o núcleo do
/// achado ("sem limite de tamanho de coluna no banco") só é verificável offline: gera-se o
/// DDL do provider SQL Server real (sem conexão) e confere-se o tipo de coluna, mesmo padrão
/// já usado em UsuarioController_EmailUnicoEUltimoAdmin_Tests
/// (DdlSqlServer_ContemIndiceUnicoEmUsuariosEmail).
/// </summary>
public class MaxLengthCamposTextoLivreDdlTests
{
    private static string GerarDdl()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=servidor-inexistente;Database=TccManagerDdl;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        using var context = new AppDbContext(options);
        return context.Database.GenerateCreateScript();
    }

    [Theory]
    [InlineData("[Titulo] nvarchar(200) NOT NULL")]
    [InlineData("[Resumo] nvarchar(4000) NOT NULL")]
    [InlineData("[MotivoRejeicao] nvarchar(2000) NULL")]
    public void TabelaTccs_ColunasDeTextoLivreTemLimiteDeTamanho(string trechoEsperado)
    {
        var ddl = GerarDdl();

        Assert.Contains(trechoEsperado, ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void TabelaAcompanhamentos_ColunaAtaTemLimiteDeTamanho()
    {
        var ddl = GerarDdl();

        Assert.Contains("[Ata] nvarchar(4000) NOT NULL", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void TabelaBanca_ColunaLocalTemLimiteDeTamanho()
    {
        var ddl = GerarDdl();

        Assert.Contains("[Local] nvarchar(300) NOT NULL", ddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Titulo] nvarchar(200) NOT NULL")]
    [InlineData("[Feedback] nvarchar(2000) NULL")]
    public void TabelaEntregas_ColunasDeTextoLivreTemLimiteDeTamanho(string trechoEsperado)
    {
        var ddl = GerarDdl();

        Assert.Contains(trechoEsperado, ddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Nome] nvarchar(200) NOT NULL")]
    [InlineData("[Instituicao] nvarchar(300) NOT NULL")]
    [InlineData("[Email] nvarchar(450) NOT NULL")]
    public void TabelaMembrosExternos_ColunasDeTextoLivreTemLimiteDeTamanho(string trechoEsperado)
    {
        var ddl = GerarDdl();

        Assert.Contains(trechoEsperado, ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void TabelaUsuarios_ColunaNomeTemLimiteDeTamanho()
    {
        var ddl = GerarDdl();

        Assert.Contains("[Nome] nvarchar(200) NOT NULL", ddl, StringComparison.Ordinal);
    }
}
