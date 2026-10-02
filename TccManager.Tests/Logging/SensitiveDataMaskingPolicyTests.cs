using Serilog.Core;
using Serilog.Events;
using TccManager.Api.Logging;
using Xunit;

namespace TccManager.Tests.Logging;

/// <summary>
/// Testes unitários diretos de SensitiveDataMaskingPolicy (IDestructuringPolicy do Serilog),
/// sem montar o pipeline completo. Verificam o mascaramento de campos sensíveis por nome,
/// a passagem intacta de objetos sem esses campos e a exclusão de tipos dos namespaces
/// System/Microsoft (conforme documentado na própria política).
/// </summary>
public class SensitiveDataMaskingPolicyTests
{
    private const string MaskedValue = "***REDACTED***";

    /// <summary>
    /// Fábrica mínima de LogEventPropertyValue: reflete o comportamento padrão do Serilog
    /// (escalares viram ScalarValue) o bastante para exercitar a política isoladamente.
    /// </summary>
    private sealed class SimplePropertyValueFactory : ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects)
            => new ScalarValue(value);
    }

    private static readonly SimplePropertyValueFactory Factory = new();

    private static Dictionary<string, LogEventPropertyValue> ComoDicionario(StructureValue structure)
        => structure.Properties.ToDictionary(p => p.Name, p => p.Value);

    private static string? ValorEscalar(LogEventPropertyValue value)
        => (value as ScalarValue)?.Value?.ToString();

    private sealed class UsuarioComSenha
    {
        public string Nome { get; set; } = string.Empty;
        public string Senha { get; set; } = string.Empty;
    }

    private sealed class UsuarioComSenhaHash
    {
        public string Nome { get; set; } = string.Empty;
        public string SenhaHash { get; set; } = string.Empty;
    }

    private sealed class CredencialComPassword
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    private sealed class RequisicaoComToken
    {
        public string Rota { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
    }

    private sealed class RequisicaoComAuthorization
    {
        public string Rota { get; set; } = string.Empty;
        public string Authorization { get; set; } = string.Empty;
    }

    private sealed class ObjetoSemCampoSensivel
    {
        public string Nome { get; set; } = string.Empty;
        public int Idade { get; set; }
    }

    private sealed class SenhaCaixaAlta
    {
        public string Nome { get; set; } = string.Empty;
        public string SENHA { get; set; } = string.Empty;
    }

    // Issue #144 (achado A2): nomes compostos que já existem hoje no código e não batiam
    // com a lista por igualdade exata.
    private sealed class ObjetoComRefreshToken
    {
        public string UsuarioId { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class ObjetoComSenhaAtualENovaSenha
    {
        public string SenhaAtual { get; set; } = string.Empty;
        public string NovaSenha { get; set; } = string.Empty;
    }

    private sealed class ObjetoComTokenHash
    {
        public string Email { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public string? ReplacedByTokenHash { get; set; }
    }

    [Fact]
    public void TryDestructure_ObjetoComSenha_MascaraApenasOValorSensivel()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new UsuarioComSenha { Nome = "Ariel", Senha = "segredo123" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["Senha"]));
        Assert.Equal("Ariel", ValorEscalar(props["Nome"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComSenhaHash_MascaraOValor()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new UsuarioComSenhaHash { Nome = "Ariel", SenhaHash = "$2a$hash" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["SenhaHash"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComPassword_MascaraOValor()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new CredencialComPassword { Login = "user", Password = "p@ss" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["Password"]));
        Assert.Equal("user", ValorEscalar(props["Login"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComToken_MascaraOValor()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new RequisicaoComToken { Rota = "/login", Token = "eyJhbGci" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["Token"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComAuthorization_MascaraOValor()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new RequisicaoComAuthorization { Rota = "/api", Authorization = "Bearer xyz" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["Authorization"]));
    }

    [Fact]
    public void TryDestructure_NomeSensivelEmCaixaAlta_MascaraIgnorandoCase()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new SenhaCaixaAlta { Nome = "Ariel", SENHA = "segredo" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["SENHA"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComRefreshToken_MascaraOValor()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new ObjetoComRefreshToken { UsuarioId = "10", RefreshToken = "abc123" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["RefreshToken"]));
        Assert.Equal("10", ValorEscalar(props["UsuarioId"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComSenhaAtualENovaSenha_MascaraOsDoisValores()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new ObjetoComSenhaAtualENovaSenha { SenhaAtual = "velha", NovaSenha = "nova" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["SenhaAtual"]));
        Assert.Equal(MaskedValue, ValorEscalar(props["NovaSenha"]));
    }

    [Fact]
    public void TryDestructure_ObjetoComTokenHashEReplacedByTokenHash_MascaraOsDoisValores()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new ObjetoComTokenHash { Email = "a@b.com", TokenHash = "hash1", ReplacedByTokenHash = "hash2" };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.True(sucesso);
        var props = ComoDicionario(Assert.IsType<StructureValue>(result));
        Assert.Equal(MaskedValue, ValorEscalar(props["TokenHash"]));
        Assert.Equal(MaskedValue, ValorEscalar(props["ReplacedByTokenHash"]));
        Assert.Equal("a@b.com", ValorEscalar(props["Email"]));
    }

    [Fact]
    public void TryDestructure_ObjetoSemCampoSensivel_NaoEDestruturado()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new ObjetoSemCampoSensivel { Nome = "Ariel", Idade = 30 };

        var sucesso = policy.TryDestructure(objeto, Factory, out var result);

        Assert.False(sucesso);
        Assert.Null(result);
    }

    [Fact]
    public void TryDestructure_TipoDoNamespaceSystem_EIgnorado()
    {
        var policy = new SensitiveDataMaskingPolicy();

        // String pertence a System; a política deve ignorar mesmo que o texto contenha "Senha".
        var sucesso = policy.TryDestructure("Senha=123", Factory, out var result);

        Assert.False(sucesso);
        Assert.Null(result);
    }

    [Fact]
    public void TryDestructure_TipoDoNamespaceMicrosoft_EIgnorado()
    {
        var policy = new SensitiveDataMaskingPolicy();

        // Tipo do namespace Microsoft.* — ignorado por política, independente de propriedades.
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var sucesso = policy.TryDestructure(logger, Factory, out var result);

        Assert.False(sucesso);
        Assert.Null(result);
    }

    [Fact]
    public void TryDestructure_ValorNulo_RetornaFalse()
    {
        var policy = new SensitiveDataMaskingPolicy();

        var sucesso = policy.TryDestructure(null!, Factory, out var result);

        Assert.False(sucesso);
        Assert.Null(result);
    }

    [Fact]
    public void TryDestructure_StructureValueUsaNomeDoTipoComoTypeTag()
    {
        var policy = new SensitiveDataMaskingPolicy();
        var objeto = new UsuarioComSenha { Nome = "Ariel", Senha = "segredo123" };

        policy.TryDestructure(objeto, Factory, out var result);

        var structure = Assert.IsType<StructureValue>(result);
        Assert.Equal(nameof(UsuarioComSenha), structure.TypeTag);
    }
}
