using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace TccManager.Api.Logging;

/// <summary>
/// Política de destructuring do Serilog que mascara propriedades sensíveis por nome
/// (Senha, SenhaHash, Password, Token, Authorization), caso algum objeto com esses
/// campos venha a ser logado por engano. Camada de defesa em profundidade — não
/// substitui a disciplina de nunca passar esses valores ao logger na origem.
/// </summary>
public class SensitiveDataMaskingPolicy : IDestructuringPolicy
{
    private const string MaskedValue = "***REDACTED***";

    private static readonly string[] SensitivePropertyNames =
    {
        "Senha",
        "SenhaHash",
        "Password",
        "Token",
        "Authorization"
    };

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        result = null!;

        if (value is null)
            return false;

        var type = value.GetType();

        if (type.Namespace is not null &&
            (type.Namespace.StartsWith("System", StringComparison.Ordinal) ||
             type.Namespace.StartsWith("Microsoft", StringComparison.Ordinal)))
        {
            return false;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var hasSensitiveProperty = properties.Any(p =>
            SensitivePropertyNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

        if (!hasSensitiveProperty)
            return false;

        var logEventProperties = new List<LogEventProperty>();

        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var isSensitive = SensitivePropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase);

            LogEventPropertyValue propertyValue;

            if (isSensitive)
            {
                propertyValue = new ScalarValue(MaskedValue);
            }
            else
            {
                object? rawValue;
                try
                {
                    rawValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                propertyValue = propertyValueFactory.CreatePropertyValue(rawValue, destructureObjects: true);
            }

            logEventProperties.Add(new LogEventProperty(property.Name, propertyValue));
        }

        result = new StructureValue(logEventProperties, type.Name);
        return true;
    }
}
