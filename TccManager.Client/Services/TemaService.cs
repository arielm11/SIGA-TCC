using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace TccManager.Client.Services;

/// <summary>
/// Fonte única de verdade do tema (claro/escuro) no lado Blazor. Persiste a preferência em
/// localStorage (chave "preferenciaTema") e troca o <c>&lt;link id="radzen-theme"&gt;</c> em
/// runtime via interop com wwwroot/js/theme.js. O primeiro paint (sem flash) é resolvido por um
/// script inline síncrono em wwwroot/index.html, que lê a mesma chave antes do WASM iniciar —
/// <see cref="InicializarAsync"/> apenas sincroniza o estado em memória com o que já foi
/// aplicado visualmente por aquele script.
/// </summary>
public class TemaService
{
    public const string TemaClaro = "standard";
    public const string TemaEscuro = "standard-dark";

    private const string ChaveLocalStorage = "preferenciaTema";

    private readonly ILocalStorageService _localStorage;
    private readonly IJSRuntime _jsRuntime;

    public TemaService(ILocalStorageService localStorage, IJSRuntime jsRuntime)
    {
        _localStorage = localStorage;
        _jsRuntime = jsRuntime;
    }

    public string TemaAtual { get; private set; } = TemaClaro;

    public bool EhEscuro => TemaAtual == TemaEscuro;

    public event Action? TemaAlterado;

    /// <summary>
    /// Lê a preferência persistida e sincroniza o estado em memória. Deve ser chamado uma única
    /// vez, no bootstrap do host (Program.cs), antes do primeiro componente renderizar.
    /// </summary>
    public async Task InicializarAsync()
    {
        string? valorSalvo;
        try
        {
            valorSalvo = await _localStorage.GetItemAsStringAsync(ChaveLocalStorage);
        }
        catch (Exception)
        {
            // localStorage pode não estar disponível (ex.: navegação privada) — mesmo
            // fallback para tema claro já usado pelo script inline em index.html.
            TemaAtual = TemaClaro;
            return;
        }

        valorSalvo = valorSalvo?.Replace("\"", string.Empty);

        TemaAtual = valorSalvo == TemaEscuro ? TemaEscuro : TemaClaro;
    }

    public async Task AlternarTemaAsync()
    {
        TemaAtual = TemaAtual == TemaClaro ? TemaEscuro : TemaClaro;

        await _localStorage.SetItemAsync(ChaveLocalStorage, TemaAtual);
        await _jsRuntime.InvokeVoidAsync("setRadzenTheme", TemaAtual);

        TemaAlterado?.Invoke();
    }
}
