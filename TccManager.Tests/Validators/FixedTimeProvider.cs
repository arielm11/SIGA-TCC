namespace TccManager.Tests.Validators;

/// <summary>
/// TimeProvider determinístico para testes: sempre retorna o "agora" fixado no
/// construtor, permitindo validar regras de data futura/passada sem depender do
/// relógio real da máquina de teste. Usado no lugar do FakeTimeProvider do pacote
/// Microsoft.Extensions.TimeProvider.Testing (não referenciado neste projeto).
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
