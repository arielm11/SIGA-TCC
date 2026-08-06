using Blazored.LocalStorage;

namespace TccManager.Tests.Client.Fakes;

/// <summary>
/// Fake em memória de <see cref="ILocalStorageService"/> (Blazored) para testar serviços do
/// Client sem JS interop real. Espelha o padrão de fakes escritos à mão já usado no projeto
/// (ex.: FakeEmailQueue), evitando introduzir uma biblioteca de mock nova.
///
/// O <see cref="Store"/> é público para que os testes possam semear valores exatamente como o
/// Blazored os grava (string JSON entre aspas) e inspecionar o que foi persistido.
/// </summary>
public class FakeLocalStorageService : ILocalStorageService
{
    public Dictionary<string, string?> Store { get; } = new();

    // Exigidos pela interface, mas não exercitados pelo TemaService.
#pragma warning disable CS0067
    public event EventHandler<ChangingEventArgs>? Changing;
    public event EventHandler<ChangedEventArgs>? Changed;
#pragma warning restore CS0067

    public ValueTask<string?> GetItemAsStringAsync(string key, CancellationToken cancellationToken = default)
        => new(Store.TryGetValue(key, out var value) ? value : null);

    public ValueTask SetItemAsync<T>(string key, T data, CancellationToken cancellationToken = default)
    {
        Store[key] = data?.ToString();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetItemAsStringAsync(string key, string data, CancellationToken cancellationToken = default)
    {
        Store[key] = data;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveItemAsync(string key, CancellationToken cancellationToken = default)
    {
        Store.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveItemsAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            Store.Remove(key);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        Store.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ContainKeyAsync(string key, CancellationToken cancellationToken = default)
        => new(Store.ContainsKey(key));

    public ValueTask<int> LengthAsync(CancellationToken cancellationToken = default)
        => new(Store.Count);

    public ValueTask<string> KeyAsync(int index, CancellationToken cancellationToken = default)
        => new(Store.Keys.ElementAt(index));

    public ValueTask<IEnumerable<string>> KeysAsync(CancellationToken cancellationToken = default)
        => new(Store.Keys.AsEnumerable());

    public ValueTask<T?> GetItemAsync<T>(string key, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Não usado por TemaService.");
}
