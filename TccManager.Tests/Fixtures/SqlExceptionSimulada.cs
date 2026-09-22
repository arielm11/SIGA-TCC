using System.Reflection;
using Microsoft.Data.SqlClient;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #88 — constrói uma <see cref="SqlException"/> com um <c>Number</c> escolhido
/// (2601/2627 = violação de índice único/chave única no SQL Server).
///
/// Por que reflexão: <see cref="SqlException"/> e <see cref="SqlError"/> não têm construtor
/// público — só o próprio driver as cria, a partir de um erro devolvido pelo servidor. A
/// suíte roda inteira em EF Core InMemory, que não implementa índices únicos relacionais
/// (P-04 da arquitetura), então a única forma de exercitar o
/// <c>catch (DbUpdateException) when (ex.InnerException is SqlException { Number: 2601 or 2627 })</c>
/// de <c>AdminBootstrapSetup</c> sem um SQL Server real é fabricar a exceção.
///
/// Limite honesto desta abordagem: ela depende de membros internos do
/// Microsoft.Data.SqlClient e pode quebrar numa atualização do pacote. Se isso acontecer,
/// <see cref="Criar"/> lança <see cref="InvalidOperationException"/> com a causa — o teste
/// falha por um motivo explícito, nunca em silêncio. O teste que a usa também assere o
/// <c>Number</c> produzido antes de usá-la, para não dar falso positivo.
/// </summary>
public static class SqlExceptionSimulada
{
    public const int NumeroViolacaoIndiceUnico = 2601;
    public const int NumeroViolacaoChaveUnica = 2627;

    public static SqlException Criar(int numero)
    {
        var erro = CriarSqlError(numero);

        var colecao = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)
            ?? throw new InvalidOperationException("Não foi possível instanciar SqlErrorCollection por reflexão.");

        var add = typeof(SqlErrorCollection)
                      .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("SqlErrorCollection.Add(SqlError) não encontrado.");
        add.Invoke(colecao, new[] { erro });

        var criar = typeof(SqlException)
                        .GetMethod(
                            "CreateException",
                            BindingFlags.Static | BindingFlags.NonPublic,
                            binder: null,
                            types: new[] { typeof(SqlErrorCollection), typeof(string) },
                            modifiers: null)
                    ?? throw new InvalidOperationException(
                        "SqlException.CreateException(SqlErrorCollection, string) não encontrado.");

        return (SqlException)criar.Invoke(null, new[] { colecao, (object)"11.0.0" })!;
    }

    private static object CriarSqlError(int numero)
    {
        // Há mais de um construtor interno de SqlError entre versões do driver; o menor
        // deles é sempre (int infoNumber, byte errorState, byte errorClass, string server,
        // string errorMessage, string procedure, int lineNumber, ...). Os argumentos são
        // preenchidos por tipo, com o número no primeiro parâmetro int.
        var construtor = typeof(SqlError)
                             .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                             .OrderBy(c => c.GetParameters().Length)
                             .FirstOrDefault()
                         ?? throw new InvalidOperationException("Nenhum construtor interno de SqlError encontrado.");

        var parametros = construtor.GetParameters();
        var argumentos = new object?[parametros.Length];
        var primeiroIntPreenchido = false;

        for (var i = 0; i < parametros.Length; i++)
        {
            var tipo = parametros[i].ParameterType;

            if (tipo == typeof(int) && !primeiroIntPreenchido)
            {
                argumentos[i] = numero;
                primeiroIntPreenchido = true;
            }
            else if (tipo == typeof(string))
            {
                argumentos[i] = string.Empty;
            }
            else if (tipo.IsValueType)
            {
                argumentos[i] = Activator.CreateInstance(tipo);
            }
            else
            {
                argumentos[i] = null;
            }
        }

        if (!primeiroIntPreenchido)
            throw new InvalidOperationException("O construtor de SqlError não tem um parâmetro int para o número do erro.");

        return construtor.Invoke(argumentos);
    }
}
