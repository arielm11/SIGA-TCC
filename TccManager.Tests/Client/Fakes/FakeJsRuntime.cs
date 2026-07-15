using Microsoft.JSInterop;

namespace TccManager.Tests.Client.Fakes;

/// <summary>
/// Fake de <see cref="IJSRuntime"/> que apenas registra as invocações de interop, sem executar
/// JS. Suficiente para verificar que TemaService chama <c>setRadzenTheme</c> com o nome de tema
/// correto. <c>InvokeVoidAsync</c> é um método de extensão que delega a
/// <c>InvokeAsync&lt;IJSVoidResult&gt;</c>, então basta capturar aqui.
/// </summary>
public class FakeJsRuntime : IJSRuntime
{
    public List<(string Identifier, object?[]? Args)> Invocacoes { get; } = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Invocacoes.Add((identifier, args));
        return new ValueTask<TValue>(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Invocacoes.Add((identifier, args));
        return new ValueTask<TValue>(default(TValue)!);
    }
}
