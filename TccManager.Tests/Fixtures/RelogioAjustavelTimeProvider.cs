namespace TccManager.Tests.Fixtures;

/// <summary>
/// <see cref="TimeProvider"/> determinístico e <b>avançável</b> para testes de integração.
///
/// Complementa o <c>FixedTimeProvider</c> de <c>TccManager.Tests.Validators</c> (que é
/// <c>internal sealed</c> e imutável, adequado apenas a validadores unitários): a janela de
/// graça de reuso de refresh token (issue #85) precisa ser exercitada nos dois sentidos —
/// dentro e fora — dentro de um mesmo teste, sem esperar segundos reais de relógio.
///
/// É o que a decisão D9 do documento de arquitetura previu ao mover o <c>AuthTokenService</c>
/// de <c>DateTime.UtcNow</c> para <see cref="TimeProvider"/> injetado.
/// </summary>
public sealed class RelogioAjustavelTimeProvider : TimeProvider
{
    private readonly object _sincronizacao = new();
    private DateTimeOffset _utcNow;

    public RelogioAjustavelTimeProvider(DateTimeOffset inicio) => _utcNow = inicio;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sincronizacao)
        {
            return _utcNow;
        }
    }

    /// <summary>Avança o relógio percebido pelo host (não afeta o relógio real da máquina).</summary>
    public void Avancar(TimeSpan intervalo)
    {
        lock (_sincronizacao)
        {
            _utcNow = _utcNow.Add(intervalo);
        }
    }
}
